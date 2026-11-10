# License server setup

The desktop client and the license service are separate applications. An HTTPS
service address has not yet been provisioned for this project. A fresh client
asks for that address; it does not create a production license service.

## Client

Use **Server settings** on the login screen. Enter the HTTPS service URL supplied
by the administrator. The client saves it under the current Windows user's
`%LOCALAPPDATA%\PMKMobileTool\pmk_license_url.txt`.

HTTP is accepted only for loopback development, for example
`http://127.0.0.1:5260`, when a local service is actually running. Saving an address
does not start or deploy a remote server. Redirects are rejected; use the final
service URL. Changing servers clears remembered credentials.

## Administrator

The service targets .NET 10; clients still need .NET 8 Desktop Runtime x86. Deploy
the service on a host with the matching ASP.NET Core runtime. Use a persistent,
restricted, writable data directory and explicitly set `DataFile` to its absolute
`licenses.json` path. Do not keep the only data copy inside a disposable build or
deployment folder.

For local development, build the server project and run its executable with
`--urls http://127.0.0.1:5260 --DataFile <absolute-data-file-path>`. Pass paths as
separate, quoted arguments. Production hosting must provide HTTPS; the checked-in
loopback listener alone is not a public service deployment.

If `AdminKey` is not configured, the service generates a random key on first start,
prints it once to the console, and stores only its SHA-256 hash in `adminkey.txt`
beside the database. Save the printed key then — the file cannot be used to recover
it. A legacy plaintext `adminkey.txt` is adopted and rewritten as a hash on the next
start. In a managed deployment (under `%ProgramData%`) the data directory is
restricted to SYSTEM and Administrators. Enter the key in the Admin app or the
service's `/admin` page. Behind a reverse proxy, list its IPs under `TrustedProxies`
so rate limiting sees the real client address; binding plain HTTP to a non-loopback
address is refused unless `AllowInsecureHttp` is set to `true` deliberately.
Approve pending registrations
before customer login. New passwords must have 8–256 characters; remembered
tokens expire after 30 days and are invalidated by password changes or blocking.

Only one service process may own a JSON database. Do not configure multiple
workers against it. API requests are serialized across read/modify/save; writes
replace the database atomically and retain the previous version in
`licenses.json.bak`. Keep separate scheduled backups: that single previous copy
is not a full backup history. A corrupt file yields HTTP 503 instead of being
replaced by an empty database. Stop the service, preserve the corrupt file, and
restore a verified backup to recover; do not delete it to silence an error.

The API limits request bodies to 16 KiB and allows 60 requests per minute per
direct peer IP. Behind a reverse proxy, clients share the proxy's quota unless a
reviewed trusted-proxy configuration is added. Do not trust arbitrary forwarded
headers. Hosting, TLS, proxy configuration, operational monitoring and backup
automation have not been deployed by these source changes.
