# Celeste + Spring Collab 2020 browser edition

An unofficial browser edition with two choices: the base game and Spring Collab 2020. It intentionally does not ship the Randomizer. The Spring Collab package includes its declared required helper mods and audio pack, but excludes its optional Randomizer dependency.

Play it at: https://a-faus.github.io/celeste-spring-collab-2020/

## Saves and privacy

Celeste, Spring Collab 2020, and the separate Celeste browser site share a browser-local save folder. This relies on both sites being under `https://a-faus.github.io`. Progress does not sync to another computer or browser profile. Save files are never included in this repository or uploaded by the site. The `backup-saves.html` page can export a private backup file on the current device.

## Hosting layout

GitHub Pages publishes only `public-site/`. Game packages are kept in `game-assets/` and fetched as public raw GitHub files; the base-game chunks are reused from the existing `a-faus/celeste-browser` repository. The site package manifest verifies each chunk with SHA-256 before installing it in browser storage.

Spring Collab 2020 is by its community contributors. Celeste and Webleste are by their respective creators. This unofficial project is not affiliated with or endorsed by them.
