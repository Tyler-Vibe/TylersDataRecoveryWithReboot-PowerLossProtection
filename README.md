# DataRecovery

A Windows desktop app that recovers deleted or lost files from a physical drive by **file carving**: it reads the raw drive sector by sector, looks for known file signatures (headers), and extracts each file it finds into a folder you choose. It doesn't rely on the file system, so it can work on drives that have been formatted, have a corrupted partition table, or show up as RAW.

It is built for large drives (multi-terabyte), and a scan can be paused, stopped, and resumed later without starting over.

## Supported file types

| Type | Default |
| --- | --- |
| MP4 / MOV video | On |
| MKV / WebM video | On |
| AVI video | On |
| WMV / ASF video | On |
| JPEG image | Off |
| PNG image | Off |
| PDF document | Off |
| ZIP archive (also DOCX / XLSX / PPTX) | Off |

You can turn each type on or off before starting a scan. Fewer types means a faster scan.

## Requirements

- Windows 10 or 11, 64-bit
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build from source)
- Administrator rights (reading a physical drive directly requires them; the app asks for elevation when it starts)
- A **second drive** with enough free space to hold the recovered files

## Build and run

From the folder that contains `DataRecovery.sln`:

```powershell
dotnet build DataRecovery.App\DataRecovery.App.csproj -c Release
.\DataRecovery.App\bin\Release\net8.0-windows\DataRecovery.exe
```

Windows will show a UAC prompt; approve it so the app can read the drive.

## How to use it

1. **Pick the source drive** you want to recover from. Click **Refresh drive list** if you plugged it in after opening the app.
2. **Pick an output folder on a different physical drive** with **Browse...**. The app refuses to write to the drive it's recovering from, because doing so can overwrite the very files you're trying to get back.
3. **Choose the file types** you want to recover.
4. Click **Start / Resume**. Progress, speed, time remaining, and found files update live.
5. Use **Pause** / **Resume** whenever you like, or **Stop** to end the scan. Progress is saved either way.

Recovered files are written to the output folder as they're found. A file that is still being written ends in `.partial`; it gets its real name only once it is fully extracted.

### Resuming a scan

The app keeps its progress in `datarecovery_session.db` inside the output folder. To pick up where you left off (after stopping, closing the app, a crash, or a reboot), select the **same output folder** and the **same source drive**, then click **Start / Resume** and choose **Yes** when asked to resume. Choosing **No** deletes the saved session and starts fresh.

## Important notes

- **Stop using the drive you're recovering from.** Every write to it (installing software, saving files, even browsing with it as the system drive) can permanently overwrite deleted data.
- **Recovered files won't have their original names or folders.** Carving finds file contents, not file-system records, so files are named by type and position on disk.
- **Some recovered files may be incomplete or won't open.** If a file was fragmented across the disk or partly overwritten, carving can only get the contiguous part. Video formats whose length can't be determined exactly are extracted with a best-guess length.
- **Scans of large drives take a long time.** A full pass over 4 TB at a typical hard-drive speed of 150 MB/s takes roughly 8 hours. Compare the *instant* and *average* speed in the window: if both are very low and the log shows `Read error` messages, the source drive may be failing.
- **If the source drive is failing**, consider imaging it first with a dedicated tool (such as GNU ddrescue) and recovering from the image instead, to avoid stressing it with repeated reads.
- This tool only **reads** the source drive; it never writes to it.

## Project layout

| Folder | Contents |
| --- | --- |
| `DataRecovery.App` | WPF user interface |
| `DataRecovery.Core` | Scanning engine: raw disk access, signature carvers, multithreaded scan pipeline, SQLite session storage |
| `DataRecovery.SmokeTest` | Console test that builds a synthetic disk image with known files, scans it, and checks a stop-and-resume scan |

Run the smoke test with:

```powershell
dotnet run --project DataRecovery.SmokeTest
```

## Disclaimer

Use at your own risk. No recovery tool can guarantee that every file is recoverable. Only scan drives you own or have permission to access.
