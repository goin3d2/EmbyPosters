define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var KINDS = ['Movie', 'Series', 'Season'];

    var BADGES = [
        { key: 'resolution', option: 'ShowResolution', name: 'Resolution (4K, 1080p, 720p…)' },
        { key: 'hdr', option: 'ShowHdr', name: 'HDR (Dolby Vision, HDR10+, HDR10, HLG)' },
        { key: 'audio', option: 'ShowAudio', name: 'Audio (Atmos, DTS:X, TrueHD, DTS-HD MA…)' },
        { key: 'imdb', option: 'ShowImdb', name: 'IMDb rating' },
        { key: 'rtcritic', option: 'ShowRtCritic', name: 'Rotten Tomatoes critics' },
        { key: 'rtaudience', option: 'ShowRtAudience', name: 'Rotten Tomatoes audience' },
        { key: 'tmdb', option: 'ShowTmdb', name: 'TMDB rating (from Emby, no API calls)' }
    ];

    function url(path) {
        return ApiClient.getUrl('PosterOverlays/' + path);
    }

    function authHeaders(extra) {
        var headers = { 'X-Emby-Token': ApiClient.accessToken() };
        for (var k in extra || {}) {
            headers[k] = extra[k];
        }
        return headers;
    }

    function getJson(path) {
        return fetch(url(path), { headers: authHeaders(), cache: 'no-store' }).then(function (r) {
            if (!r.ok) {
                throw new Error(path + ': HTTP ' + r.status);
            }
            return r.json();
        });
    }

    function postJson(path, body) {
        return fetch(url(path), {
            method: 'POST',
            headers: authHeaders({ 'Content-Type': 'application/json' }),
            body: JSON.stringify(body)
        });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        var self = this;
        this.config = null;
        this.order = BADGES.map(function (b) { return b.key; });
        this.previewItems = { Movie: [], Series: [], Season: [] };
        this.picked = { Movie: null, Series: null, Season: null };
        this.objectUrls = {};
        this.previewSeq = 0;
        this.previewTimer = null;

        view.querySelector('form').addEventListener('submit', function (e) {
            e.preventDefault();
            self.save();
            return false;
        });

        // Any change anywhere in the form re-renders the previews (debounced).
        var form = view.querySelector('form');
        form.addEventListener('input', function (e) {
            self.updateOutputs();
            if (e.target && e.target.getAttribute('data-key') !== 'TmdbApiKey' && e.target.getAttribute('data-key') !== 'MdbListApiKey') {
                self.schedulePreview();
            }
        });
        form.addEventListener('change', function () {
            self.updateOutputs();
            self.schedulePreview();
        });

        view.querySelector('.po-badges').addEventListener('click', function (e) {
            var btn = e.target.closest('button[data-move]');
            if (!btn) {
                return;
            }
            var key = btn.getAttribute('data-badge');
            var i = self.order.indexOf(key);
            var j = i + parseInt(btn.getAttribute('data-move'), 10);
            if (i < 0 || j < 0 || j >= self.order.length) {
                return;
            }
            var tmp = self.order[i];
            self.order[i] = self.order[j];
            self.order[j] = tmp;
            self.renderBadgeList(self.collect());
            self.schedulePreview();
        });

        ['.selPreviewSource', '.selPreviewData', '.selPreviewStatus'].forEach(function (sel) {
            view.querySelector(sel).addEventListener('change', function () {
                self.schedulePreview(0);
            });
        });

        view.querySelector('.btnSavePreview').addEventListener('click', function () {
            self.save();
        });

        view.querySelector('.btnShuffle').addEventListener('click', function () {
            self.shuffle();
            self.schedulePreview(0);
        });

        view.querySelector('.btnRunRefresh').addEventListener('click', function () {
            self.runTask('PosterOverlaysRefreshData', 'Refreshing overlay data. Save your API keys first if you just entered them.');
        });

        view.querySelector('.btnDownloadIcons').addEventListener('click', function () {
            self.runTask('PosterOverlaysDownloadIcons', 'Downloading icons…');
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);

        var self = this;
        loading.show();

        getJson('Settings').then(function (config) {
            self.config = config;
            self.order = (config.BadgeOrder || '').split(/[\s,;]+/).filter(function (k) {
                return BADGES.some(function (b) { return b.key === k; });
            });
            BADGES.forEach(function (b) {
                if (self.order.indexOf(b.key) < 0) {
                    self.order.push(b.key);
                }
            });
            self.fill(config);
            loading.hide();
            self.refreshStatus();
            return self.loadPreviewItems();
        }).then(function () {
            self.schedulePreview(0);
        }).catch(function (err) {
            loading.hide();
            self.showStatus('Could not load settings: ' + err.message, true);
        });
    };

    View.prototype.destroy = function () {
        for (var k in this.objectUrls) {
            URL.revokeObjectURL(this.objectUrls[k]);
        }
        this.objectUrls = {};
        clearTimeout(this.previewTimer);
        BaseView.prototype.destroy.apply(this, arguments);
    };

    // ---------------------------------------------------------------- form <-> options

    View.prototype.fields = function () {
        return Array.prototype.slice.call(this.view.querySelectorAll('form [data-key]'));
    };

    View.prototype.fill = function (config) {
        this.fields().forEach(function (el) {
            var key = el.getAttribute('data-key');
            var value = config[key];
            if (el.type === 'checkbox') {
                el.checked = !!value;
            } else if (el.type === 'color') {
                el.value = /^#[0-9a-f]{6}$/i.test(value || '') ? value : '#000000';
            } else {
                el.value = value === null || value === undefined ? '' : value;
            }
        });
        this.renderBadgeList(config);
        this.updateOutputs();
    };

    View.prototype.collect = function () {
        var options = Object.assign({}, this.config || {});
        this.fields().forEach(function (el) {
            var key = el.getAttribute('data-key');
            if (el.type === 'checkbox') {
                options[key] = el.checked;
            } else if (el.type === 'number' || el.type === 'range') {
                var n = parseInt(el.value, 10);
                options[key] = isNaN(n) ? (this.config || {})[key] : n;
            } else if (el.type === 'color') {
                options[key] = el.value.toUpperCase();
            } else {
                options[key] = el.value;
            }
        }, this);

        var list = this.view.querySelector('.po-badges');
        BADGES.forEach(function (b) {
            var box = list.querySelector('input[data-badge="' + b.key + '"]');
            if (box) {
                options[b.option] = box.checked;
            }
        });
        options.BadgeOrder = this.order.join(', ');
        return options;
    };

    View.prototype.renderBadgeList = function (config) {
        var list = this.view.querySelector('.po-badges');
        var last = this.order.length - 1;
        list.innerHTML = this.order.map(function (key, i) {
            var badge = BADGES.filter(function (b) { return b.key === key; })[0];
            return '<li>' +
                '<label><input type="checkbox" is="emby-checkbox" data-badge="' + key + '"' + (config[badge.option] ? ' checked' : '') + ' /><span>' + badge.name + '</span></label>' +
                '<button is="emby-button" type="button" class="po-move" data-move="-1" data-badge="' + key + '" title="Move up"' + (i === 0 ? ' disabled' : '') + '>▲</button>' +
                '<button is="emby-button" type="button" class="po-move" data-move="1" data-badge="' + key + '" title="Move down"' + (i === last ? ' disabled' : '') + '>▼</button>' +
                '</li>';
        }).join('');
    };

    View.prototype.updateOutputs = function () {
        var view = this.view;
        Array.prototype.forEach.call(view.querySelectorAll('output[data-for]'), function (out) {
            var input = view.querySelector('[data-key="' + out.getAttribute('data-for') + '"]');
            var key = out.getAttribute('data-for');
            out.textContent = input ? input.value + (/Percent|Opacity/.test(key) ? '%' : '') : '';
        });
    };

    // ---------------------------------------------------------------- save, tasks, status

    View.prototype.save = function () {
        var self = this;
        loading.show();
        postJson('Settings', this.collect()).then(function (r) {
            if (!r.ok) {
                throw new Error('HTTP ' + r.status);
            }
            return r.json();
        }).then(function (saved) {
            self.config = saved;
            loading.hide();
            Dashboard.processServerConfigurationUpdateResult();
            self.refreshStatus();
        }).catch(function (err) {
            loading.hide();
            self.showStatus('Could not save: ' + err.message, true);
        });
    };

    View.prototype.runTask = function (taskKey, message) {
        var self = this;
        ApiClient.getJSON(ApiClient.getUrl('ScheduledTasks')).then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === taskKey; })[0];
            if (!task) {
                throw new Error('task not found');
            }
            return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('ScheduledTasks/Running/' + task.Id) });
        }).then(function () {
            self.showStatus(message + ' Progress is under Scheduled Tasks.', false);
            setTimeout(function () { self.refreshStatus(); }, 8000);
        }).catch(function (err) {
            self.showStatus('Could not start the task: ' + err.message, true);
        });
    };

    View.prototype.refreshStatus = function () {
        var self = this;
        getJson('Status').then(function (s) {
            var mdb = s.NeedsMdbList ? 'MDBList key: not set' : 'MDBList: not needed with current badges';
            if (s.MdbListConfigured && s.NeedsMdbList) {
                mdb = typeof s.MdbListDailyLimit === 'number'
                    ? 'MDBList: ' + s.MdbListUsedToday + ' of ' + s.MdbListDailyLimit + ' calls used today (' + s.MdbListReserve + ' kept in reserve)'
                    : 'MDBList key: set (quota not available yet)';
            }
            var parts = [
                'Icons: ' + s.IconsAvailable + ' of ' + s.IconsExpected,
                'Titles with overlay data: ' + s.ItemsCached,
                'IMDb ratings: ' + (s.ImdbRatingsCached || 0) + (s.ImdbDatasetFetchedUtc ? ' (file from ' + new Date(s.ImdbDatasetFetchedUtc).toLocaleString() + ')' : ' (not downloaded yet)'),
                'Titles with MDBList ratings: ' + (s.RatingsCached || 0),
                'TMDB key: ' + (s.TmdbConfigured ? 'set' : 'not set'),
                mdb
            ];
            self.showStatus(parts.join(' · '), false);
        }).catch(function () { });
    };

    View.prototype.showStatus = function (text, isError) {
        var el = this.view.querySelector('.po-status');
        el.textContent = text;
        el.classList.toggle('po-error', !!isError);
    };

    // ---------------------------------------------------------------- previews

    View.prototype.loadPreviewItems = function () {
        var self = this;
        return getJson('PreviewItems').then(function (items) {
            self.previewItems = items || self.previewItems;
            self.shuffle();
        }).catch(function () {
            self.previewItems = { Movie: [], Series: [], Season: [] };
        });
    };

    View.prototype.shuffle = function () {
        var self = this;
        KINDS.forEach(function (kind) {
            var list = self.previewItems[kind] || [];
            if (!list.length) {
                self.picked[kind] = null;
                return;
            }
            var next = list[Math.floor(Math.random() * list.length)];
            if (list.length > 1 && self.picked[kind] && next.Id === self.picked[kind].Id) {
                next = list[(list.indexOf(next) + 1) % list.length];
            }
            self.picked[kind] = next;
        });
    };

    View.prototype.schedulePreview = function (delay) {
        var self = this;
        clearTimeout(this.previewTimer);
        this.previewTimer = setTimeout(function () { self.renderPreviews(); }, delay === undefined ? 250 : delay);
    };

    View.prototype.renderPreviews = function () {
        var self = this;
        var view = this.view;
        var seq = ++this.previewSeq;
        var options = this.collect();
        var useLibrary = view.querySelector('.selPreviewSource').value === 'library';
        var useSample = view.querySelector('.selPreviewData').value === 'sample';
        var status = view.querySelector('.selPreviewStatus').value;
        view.querySelector('.btnShuffle').disabled = !useLibrary;
        view.querySelector('.selPreviewStatus').disabled = !useSample;

        KINDS.forEach(function (kind) {
            var figure = view.querySelector('figure[data-kind="' + kind + '"]');
            var frame = figure.querySelector('.po-frame');
            var item = useLibrary ? self.picked[kind] : null;
            figure.querySelector('.po-name').textContent = item ? item.Name : (useLibrary ? '(no poster found, placeholder)' : '');
            frame.classList.add('loading');

            fetch(url('Preview'), {
                method: 'POST',
                headers: authHeaders({ 'Content-Type': 'application/json' }),
                body: JSON.stringify({ Options: options, Kind: kind, ItemId: item ? item.Id : '', UseSample: useSample, Status: status })
            }).then(function (r) {
                if (!r.ok) {
                    throw new Error('HTTP ' + r.status);
                }
                return r.blob();
            }).then(function (blob) {
                if (seq !== self.previewSeq) {
                    return; // a newer preview is on its way
                }
                var objectUrl = URL.createObjectURL(blob);
                if (self.objectUrls[kind]) {
                    URL.revokeObjectURL(self.objectUrls[kind]);
                }
                self.objectUrls[kind] = objectUrl;
                frame.querySelector('img').src = objectUrl;
                frame.classList.remove('loading');
            }).catch(function (err) {
                if (seq === self.previewSeq) {
                    frame.classList.remove('loading');
                    self.showStatus('Preview failed: ' + err.message, true);
                }
            });
        });
    };

    return View;
});
