# Security

Please do not include Pi-hole passwords, query-log exports, hostnames, IP addresses, or other private network details in issues or pull requests.

## Deployment boundary

The application is designed for use on a trusted LAN. It does not provide authentication or authorization: anyone who can reach the HTTP listener can read and modify the shared review state and submit Pi-hole connection requests. The default Compose configuration publishes unauthenticated plain HTTP on port `5178` on all host interfaces. Use a firewall to restrict it to the trusted LAN, and do not port-forward it to the public Internet.

If broader access is essential, put it behind an authenticated HTTPS reverse proxy. This is an additional access-control layer, not a change to the application's trust model: the application accepts a user-supplied HTTP/HTTPS Pi-hole URL and makes server-side requests to it, so it should not be treated as a general public service. Use HTTPS for the Pi-hole connection where available because HTTP exposes the password and session traffic on the network.

The Pi-hole connection password is accepted only to create a short-lived session; it is not persisted by the application.

For a suspected security vulnerability, avoid posting sensitive details publicly. Contact the repository owner privately through the GitHub repository’s security contact options.
