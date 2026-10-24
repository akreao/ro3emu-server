# Play on your own PC

The full guide behind the README's three steps.

1. Download **`RO3Emu-win-x64.zip`** from the [latest release](https://github.com/akreao/ro3emu-server/releases/latest)
   and extract it anywhere. A release is cut every day main changes, and its notes list what changed. The very newest
   build of main is the rolling [`launcher` release](https://github.com/akreao/ro3emu-server/releases/tag/launcher):
   it gets every daily release and hand-pushed `v*` tag, and is also rebuilt right away when the launcher, patcher or
   client DLLs change.
2. Double-click **`RO3Emu.exe`**. Nothing to install: no .NET, Java or Node.js. If Windows says "Windows protected
   your PC" (RO3Emu is not signed), click *More info*, then *Run anyway*.
3. The first time, it finds your Ragnarok 3: Genesis game folder (or asks for the one with `ro3.exe`), prepares the
   game data it needs (about a minute), starts the server and the game. Next time it only starts the server and the
   game.
4. Log in with any account name and password: the account is created on first login. Keep the RO3Emu window open
   while you play; press **Q** there (or close it) to stop the server.

**Don't have the game?** RO3Emu.exe downloads it from the Genesis update servers (about 24 GB) and checks every
file against the publisher's signature: it offers this when it finds no game folder, or run `RO3Emu.exe install`.
RO3Emu never hosts game files. Tested with the Genesis client releases of 7, 16, 21 and 23 October 2026 (2026.10.23.1,
the current one); the 20 October release passes `RO3Emu.exe check` and the data export. Genesis is a private server,
not the official one: the official Ragnarok Online 3 server has not launched yet.

| Command | What it does |
|---|---|
| `RO3Emu.exe` | play on your own server (same as `RO3Emu.exe start`) |
| `RO3Emu.exe join <address>` | play on someone else's server (`host`, `host:port` or `http://host:port`) |
| `... --account <name>` | (`start`, `host`, `join`) log in for the player: the game skips its login and server screens and opens on character select (the password is asked, or taken from `--password` or `RO3EMU_PASSWORD`) |
| `RO3EmuDesktop.exe` | (in the same zip) the window launcher, works on its own without RO3Emu.exe: server list, login, *Update DLLs* (newest CI build) and *Play* (gets a newer CI build of the DLLs when there is one, then the same patch, login and start as `join`) |
| `RO3Emu.exe host` | your server, open to friends (`--gm <account>` for GM commands) |
| `RO3Emu.exe install [folder]` / `update [folder]` | the game from the Genesis update servers, every file checked |
| `RO3Emu.exe restore` | undo the patch: the game talks to the Genesis servers again |
| `RO3Emu.exe check` | does this game version fit this RO3Emu? (changes nothing) |
| `RO3Emu.exe diag` | makes `RO3Emu-diagnostics.zip` for a bug report |
| `RO3Emu.exe delete-character [address]` | delete one of your characters (the game has no delete button); log out first |

**What it changes.** In the game folder: a few DLLs in `ro3_Data\Managed` (the originals are kept in `ro3emu-backup`)
and `ro3emu\loc_en.tsv` (English texts, built from your own game files). Next to RO3Emu.exe: `data\` (game data and
your characters), `logs\` and `RO3Emu.settings.json`.

> [!WARNING]
> Don't let the publisher's launcher repair a patched game folder: it puts the original DLLs back. Run RO3Emu.exe
> again and it patches the game again.
