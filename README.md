# Pi-hole Domain Review

A local review queue for Pi-hole query logs. It groups raw DNS requests into unique domains so you can mark recognised domains as known, filter them out, and add the remaining domains to a local block list for later action.

This is a self-hosted, LAN-oriented tool. It is not an Internet-facing or multi-user service.

> **Security warning:** The application has no built-in authentication or authorization. Anyone who can reach its HTTP port can read and change the shared review state, and can submit Pi-hole connection requests. Run it only on a trusted, firewall-restricted LAN; do not port-forward it to the Internet. If broader access is essential, use an authenticated HTTPS reverse proxy and keep the Pi-hole URL limited to a trusted destination. The default Compose setup uses plain HTTP and publishes port `5178` on all host interfaces.

## Current MVP

- Stores known domains, the local block list, sync history, and investigation notes server-side in `data/review-state.json`.
- Stores connection and UI configuration server-side in `data/config.json`. Query-log data is fetched live and is not loaded from cache.
- Connects to Pi-hole v6 using a short-lived session. The supplied password is not persisted.
- Imports paginated `/api/queries` data and aggregates query count, clients, first/last seen, and status per domain.
- Supports date range selection and on-disk history.
- Hides domains that Pi-hole has already blocked by default; the preference is persisted with the configuration and can be turned off.
- Adds domains to the app’s local block list after confirmation.
- Syncs local block-list decisions to Pi-hole’s exact deny list after a second confirmation, while leaving existing Pi-hole entries alone and reporting any failures. Successful additions are tracked as sent by this tool.
- Lets you mark a domain for investigation with a note; investigation notes are persisted and shown in their own tab.

## Run in Docker on a Raspberry Pi

Raspberry Pi OS 64-bit is recommended. The image uses the matching .NET 10 container architecture, so the same files work on an ARM64 Pi and can also be built for ARM64 with Docker Buildx.

Requirements: Docker Engine with the Compose plugin, SSH access for the deployment script, and Pi-hole v6.

Copy the project to the Pi, then run:

```bash
mkdir -p data
docker compose up -d --build
```

Open `http://<raspberry-pi-hostname>:5178/` from a device on the same trusted network. In the app, enter the Pi-hole URL as seen from the container. If Pi-hole is running on the same Pi, `http://host.docker.internal:<pihole-port>` is available through the Compose host mapping; `localhost` refers to the review container itself. The default port is unauthenticated plain HTTP, so restrict it with the Pi's firewall and do not expose it beyond the LAN.

The bind-mounted `data/` directory contains the persistent server-side configuration and review decisions. It is not included in the image, so rebuilding or replacing the container does not remove them. Use `docker compose logs -f` to inspect startup and Pi-hole connection errors, and `docker compose restart` after configuration changes. If you enter a Pi-hole password while using an `http://` Pi-hole URL, that credential and the resulting session travel over the local network without TLS; use HTTPS where available.

To build on another machine and target a 64-bit Raspberry Pi:

```bash
docker buildx build --platform linux/arm64 -t pihole-domain-review:local --load .
docker compose up -d
```

### Push from Windows over SSH

If the Pi already has Docker and the SSH user can run `docker compose` without a separate `sudo` password prompt, the included script pushes the project and starts the container through one SSH connection:

```powershell
.\deploy\push-to-raspberrypi.ps1 -PiHost <raspberry-pi-hostname> -PiUser <ssh-user>
```

OpenSSH prompts for the SSH password once. The script streams the project archive directly to the Pi, excludes the local `data/` directory, and runs `docker compose up -d --build` in `~/pihole-domain-review`. Existing data on the Pi is preserved. Use `-PiPort` if SSH is not on port 22 or `-RemoteDirectory` to change the deployment folder.

### Push from Linux, macOS, or WSL over SSH

The portable Bash script has the same behavior and uses positional arguments:

```bash
bash ./deploy/push-to-raspberrypi.sh <raspberry-pi-hostname> <ssh-user>
```

Optional third and fourth arguments set the SSH port and remote directory:

```bash
bash ./deploy/push-to-raspberrypi.sh <host> <ssh-user> <ssh-port> <remote-directory>
```

It requires local `ssh` and `tar`, plus Docker Compose on the Pi. The SSH user must be able to run Docker without an interactive `sudo` password prompt. The script excludes Git metadata, runtime data, build output, environment files, and private-key/certificate files from the deployment archive.

## Run locally

```powershell
dotnet run
```

Then open the local URL printed by ASP.NET Core. Pi-hole v6 must be reachable from the machine running this app. A password is optional when Pi-hole allows unauthenticated API access.

Run the unit tests with:

```powershell
dotnet test tests\PiHoleTracking.Tests\PiHoleTracking.Tests.csproj
node --test tests\js\app.test.js
```

The app keeps “Known” separate from Pi-hole’s rules. Marking a domain known only removes it from the local review queue. Adding a domain to the local block list records your decision without changing traffic; choosing “Sync to Pi-hole” explicitly adds those local entries as exact deny-list domains. Sync is additive and does not remove existing Pi-hole entries.

## Data and configuration

Runtime state is intentionally excluded from Git. The ignored `data/` directory contains the server-side configuration and review decisions; query-log data is not persisted by the app. The saved Pi-hole URL may still reveal a private hostname or address. Do not commit Pi-hole passwords, query-log exports, private network details, `.env` files, or private-key/certificate files.

## Notes

Pi-hole controls the available history through its query database retention settings. Very large imports are capped at 100 pages of 1,000 records in this first version; the UI reports when that cap is reached so a narrower date range can be used.

## License

This project is licensed under the [MIT License](LICENSE). See the [LICENSE](LICENSE) file for the full text.

## Contributing

Semantic commits are required. See [CONTRIBUTING.md](CONTRIBUTING.md) for the commit format and examples.
