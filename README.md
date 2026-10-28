# EmbyPosters

Draws a full-width **series status banner** (Airing, Returning, Ended, Canceled, Upcoming) and a vertical **badge stack on the left** (resolution, HDR / Dolby Vision, Atmos and other audio, IMDb, Rotten Tomatoes) on movie, series and season posters.

Built and tested against **Emby Server 4.11.0.10** (.NET 8). Original artwork is never modified: Emby runs the plugin as an image enhancer and caches the result.

## Install

1. Download `PosterOverlays.dll` from the [Releases](https://github.com/goin3d2/EmbyPosters/releases) page (or build it, below) and copy it into Emby's `plugins` folder (`<programdata>/plugins`, for example `/config/plugins` in the Docker image).
2. Restart Emby.
3. Open **Settings > Plugins > Poster Overlays**, then paste in your TMDB key and MDBList key and save. The settings page shows live previews of a movie, series and season poster as you change options. It uses random posters from your library (use **Shuffle** to change them) or neutral placeholders. Previews use your unsaved settings, and nothing changes on the server until you save.
4. Open **Scheduled Tasks > Poster Overlays** and run both tasks:
   - **Download poster overlay icons**. This also runs once on every startup, but only to fill in missing files.
   - **Refresh poster overlay data**. After the first run it repeats every 6 hours by default.
5. Posters update the next time a client asks for them. If a client has old images cached, refresh it.

## Where the data comes from

| Badge | Source | Notes |
|---|---|---|
| Status banner | TMDB `/tv/{id}` | Returning Series becomes **Airing** if an episode aired, or will air, within the airing window (default 15 days); otherwise **Returning**. Ended and Canceled map directly. Planned, In Production and Pilot become **Upcoming**. Without a TMDB key, Emby's own Continuing/Ended status is used. |
| Resolution | Emby media streams | Width counts too, so 3840×1600 scope films are still 4K. |
| HDR / DV | Emby `ExtendedVideoType` | Dolby Vision > HDR10+ > HDR10 > HLG. |
| Audio | Track codec, plus track title and file name | Emby stores no Atmos/DTS:X flag, so those come from track titles ("TrueHD Atmos 7.1") or the release name ("DDP5.1.Atmos", "DTS-X"). Only TrueHD and E-AC3 can be Atmos; an AAC track titled "(from E-AC3 Atmos)" doesn't count. Plain DD, DD+ and DTS are hidden unless you turn on *Include basic audio*. |
| IMDb | IMDb's daily ratings file (`datasets.imdbws.com/title.ratings.tsv.gz`, personal/non-commercial use) | One ~8 MB download at most once a day covers every title; only library titles are kept. MDBList can be chosen instead. |
| TMDB | Emby's community rating | Already stored by Emby (TMDB's score with the default metadata providers). No API calls. Off by default. |
| RT critics, RT audience | MDBList `api.mdblist.com/{imdb or tmdb}/{movie or show}/{id}` | Only called when a Rotten Tomatoes badge is on. One call per title, cached. Each run reads the account's remaining daily quota and uses all but the reserve (default 100). Titles with no ratings come first, newest first, movies and series mixed; stale refreshes follow (recent titles every 3 days, older every 60). |

Series and seasons summarize their episodes using the *Series quality rule*: latest season (default), best available, or most episodes. Versions of the same episode count once (their best). Versions of the same movie (Emby groups them into one title) all get the best quality found across the set, so a movie with 1080p and 4K files shows 4K whichever version Emby picks.

## Icons

Icons live in the plugin's data folder. That's normally `plugins/PosterOverlays`, next to Cover Art's `plugins/CoverArt`; the icon task logs the exact path.

```
plugins/PosterOverlays/icons/           downloaded pack
plugins/PosterOverlays/icons/custom/    your own files (always win)
plugins/PosterOverlays/overlay-data.json   cached status, ratings and quality
plugins/PosterOverlays/settings.json       your settings
```

The default pack is Kometa's overlay images, pinned to commit `e1327dc`. You can change the source URL in settings. To override one icon, drop a PNG with the same name into `icons/custom`. These are the names:

```
res_4k res_1080p res_720p res_576p res_480p
hdr_dv hdr_hdr10plus hdr_hdr10 hdr_hlg
audio_truehd_atmos audio_plus_atmos audio_atmos audio_dtsx audio_dtshdma audio_truehd audio_ddplus audio_dts audio_dd
imdb rt_critic_fresh rt_critic_rotten rt_audience_fresh rt_audience_rotten
```

Any badge without an icon is drawn as text. RT counts as fresh at 60% and above.

## Build from source

The project targets `net8.0` and references the server's own assemblies, so no NuGet packages are needed. Copy the server's `system` folder next to the repo and build:

```bash
docker cp <your-emby-container>:/system ./emby-lib
dotnet build src/PosterOverlays -c Release
# output: src/PosterOverlays/bin/Release/net8.0/PosterOverlays.dll
```

Or pass `-p:EmbyLibDir=/path/to/system`.

## Layout

```
src/PosterOverlays/
  Plugin.cs                    plugin entry, settings.json, data folder, page registration
  PluginOptions.cs             all settings, with clamping/validation
  Configuration/configPage.*   settings page with live previews
  Api/PosterOverlaysApi.cs     admin-only endpoints: settings, status, preview items, preview render
  PosterOverlayEnhancer.cs     IImageEnhancer: cache key + drawing
  Drawing/OverlaySpec.cs       what to draw for an item (no Emby types)
  Drawing/OverlayRenderer.cs   SkiaSharp banner + badge renderer
  Drawing/PreviewRenderer.cs   preview posters (sample data, placeholders)
  Services/MediaInfoClassifier.cs   resolution / HDR / audio detection
  Services/StatusClassifier.cs      TMDB status mapping
  Services/ApiClients.cs            TMDB and MDBList clients
  Icons/IconManager.cs         icon folder, downloads, key mapping
  Data/OverlayStore.cs         JSON cache of per-item results and API lookups
  Tasks/                       the two scheduled tasks
```

## Notes

- **Cover Art plugin.** If Cover Art also draws resolution or audio overlays, they will stack. This plugin runs last, so its badges sit on top. Turn off the overlapping Cover Art treatments for movies and series.
- New movies get quality badges immediately. Ratings, and anything for new series, arrive with the next refresh run.
- Episode thumbnails are not touched.

Default icons are from [Kometa](https://github.com/Kometa-Team/Kometa) (MIT). The bundled font is DejaVu Sans Condensed Bold (Bitstream Vera / DejaVu license).

## License

MIT, see [LICENSE](LICENSE). Not affiliated with Emby, IMDb, Rotten Tomatoes, TMDB, MDBList or Kometa. Logos downloaded by the icon task belong to their owners; IMDb's ratings file is used under IMDb's terms for personal, non-commercial use.
