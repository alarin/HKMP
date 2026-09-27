# Skins

`skins.json` lists the skins we play with and pins their HKMP skin IDs. The skins themselves are not stored
here, `sync-skins.ps1` downloads them by name from the sources listed in the [HKSkins](https://hkskins.art)
catalog and installs them into `Mods\HKMP\Skins` of the Steam install:

```powershell
powershell -ExecutionPolicy Bypass -File skins\sync-skins.ps1
```

Run it on every machine after pulling, then restart the game. Same `skins.json` means same IDs everywhere.

To add a skin, append `{ "name": "<name as on hkskins.art>", "id": <free id 1-255> }` and run the script.
Optional fields:

- `source` — download link to use instead of the catalog one (for skins hosted on Discord and the like, or
  to take a skin from a multi-skin pack)
- `folder` — folder name of the skin inside the archive, when it cannot be matched by name

Google Drive files and folders are downloaded automatically. Skins from other hosts have to be put into
`Skins\<name>` by hand, the script then only writes their `id.txt`. Downloads are cached in
`%LOCALAPPDATA%\hkmp-skin-sync`, `-Force` reinstalls everything.
