# Who has GM (`Ro3Emu:GmAccounts`)

GM commands are the `!` lines in chat (`!help`), the client's GM panel (30300) and the GM transmit/inspect messages
(1031/1034). One check decides all of them: `GmCommands.Allowed`.

| `GmAccounts` | `GmOpenWhenEmpty` | in game |
|---|---|---|
| listed accounts | any | only those accounts, spelled as stored (an account name is one account in any letter case; the list matches its stored spelling) |
| empty | unset, host not started as Production | everyone (tests, `tools/run-server.ps1`, local play) |
| empty | unset, host started as **Production** | **nobody** |
| empty | `true` / `false` | everyone / nobody (an explicit value wins over the environment) |

"Started as Production" means the environment was set explicitly: `ASPNETCORE_ENVIRONMENT=Production`,
`DOTNET_ENVIRONMENT=Production` or `--environment Production`. ASP.NET's own default when nothing is set is also
called Production, but it does not count, so tests and local runs keep "empty = everyone".

At startup the server logs who has GM. With an empty list it warns loudly in both cases that matter:

- Production and nobody has GM: `!!! Production: Ro3Emu:GmAccounts is empty, so NO account has GM commands in game.`
- GM open to everyone on a public address (`PublicHost` not loopback): `!!! Ro3Emu:GmAccounts is empty and GM is open: EVERY account can use GM commands in game ...`

The [web panel](panel.md)'s admin area uses the same list, with one difference: an empty list means nobody is an
admin there, whatever `GmOpenWhenEmpty` says. Its GM console runs a command for any character in the game: the
admin's rights count there, not those of the character's account.

## Configuration

Like every `Ro3Emu:` option, the list can be set in `appsettings.json`:

```json
{
  "Ro3Emu": {
    "GmAccounts": [ "your-admin-login" ]
  }
}
```

or through environment variables, one per account: `Ro3Emu__GmAccounts__0=your-admin-login`,
`Ro3Emu__GmAccounts__1=another-login`, and so on. The value is the game login (account name), not a character name.

## The loopback tool

`POST http://127.0.0.1:5380/ro3emu/gm?account=<account>` (body `!warp 10111 150 190`) answers only on loopback and
is authorized by that origin, not by the target account: it runs for any online account, whether `GmAccounts` is empty
or lists other accounts, so an admin panel on the server machine can act on ordinary players. Production does not
close it. The trust boundary is the host: any process that can reach loopback on the server machine (including a
reverse proxy on it, see [security](../systems/security.md)) can run GM commands as any online account. The relay
does not widen in-game access: the same account's `!` chat lines and GM panel still follow the table above.

It answers 200 with the command's reply as a JSON string (`"accept 10101010: ..."`; several replies are joined with a
newline; `"done"` when the command replied nothing). The reply still goes to the player's chat too. 400 `not in game`
when the account has no character in the world, 404 from any address but loopback, 503 while the server is not
accepting commands.

### Quest debugging

Two loopback-only reads (404 from any other address) help with `!quest accept|finish|done|drop <taskId> | reset`:

- `GET /ro3emu/quests`: every quest of TaskConfig, sorted by id, `[{"id": 10101010, "name": "..."}]` (`[]` without
  the table).
- `GET /ro3emu/quests?account=<account>`: the quests of that account's character in the world (account matched
  without regard to case; 400 `not in game` otherwise):

  ```json
  {"account": "tester", "role": "Alice", "quests": [
    {"id": 10101010, "name": "...", "status": "running", "progress": [{"done": 0, "need": 1}], "completed": 0}]}
  ```

  Every quest the character holds or has delivered, sorted by id. `status` is the TaskState in lower case (`running`,
  `finish`, `delivery`, ...; `delivery` for a quest only counted in CompleteTaskNum), `progress` has one entry per
  target and `completed` is how many times it was delivered.

## A public server

On a server that other people can reach, start the host as Production and list the GM accounts. With systemd, a
drop-in such as `/etc/systemd/system/ro3emu.service.d/gm.conf` (adjust the unit name to yours) needs both lines:

```ini
[Service]
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Ro3Emu__GmAccounts__0=your-admin-login
```

More GMs: `Ro3Emu__GmAccounts__1=...`. Passwords never go in the unit, the repository or these docs. After
`systemctl daemon-reload` and a restart, the log shows `GM accounts: your-admin-login`.
