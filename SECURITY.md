# Security

Please do not include Pi-hole passwords, query-log exports, hostnames, IP addresses, or other private network details in issues or pull requests.

The application is designed for use on a trusted LAN. It does not provide its own user authentication, and the Pi-hole connection password is accepted only to create a short-lived session; it is not persisted by the application. Do not expose the application directly to the public internet. Use a firewall or an authenticated reverse proxy if broader access is required.

For a suspected security vulnerability, avoid posting sensitive details publicly. Contact the repository owner privately through the GitHub repository’s security contact options.
