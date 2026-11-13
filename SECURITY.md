# Security

## Reporting

Please do not open a public issue for a security problem. Use GitHub's **Report a vulnerability**
(Security ▸ Advisories) on this repository, or email the maintainer listed in the repository
profile. You will get an acknowledgement, and credit in the advisory if you want it.

## Threat model, such as it is

VirtualHub runs a web server on your own machine and serves it to your own headset over your own
LAN. That is the whole deployment model, and it shapes what is and is not a vulnerability.

**In scope**

- Path traversal or file disclosure through the server's routes (`/models/...`, `/scene/...`).
- Anything that lets a page on another origin read the library or the exported models.
- A denial of service that breaks the server with a malformed request.
- Vulnerabilities in the vendored three.js or in a dependency.

**Out of scope, by design**

- The server has **no authentication** and binds to the LAN when serving HTTPS. Anyone on the same
  network can read the library. Do not expose the port to the internet or forward it on a router.
- The HTTPS certificate is self-signed and generated on the machine. That is what makes the
  zero-configuration headset path work; it is not a defect.
- Exported models and their metadata are plain files under `%LOCALAPPDATA%`; protecting them is the
  operating system's job.
- Reports that require an attacker to already run code on the machine.

## Hardening already in place

- `/models/` paths are resolved and confined to the library; `..`, encoded dots and separators
  return 404. Both servers are tested for this, including the encoded form Kestrel would otherwise
  normalise before routing.
- The server sends `no-store` for scene data, so a stale or tampered file is not cached by the
  headset.
- No secrets are stored in the repository, and the generated certificates and private keys are
  ignored by git.
- The server never writes to the library; publishing is done by the plugins, which copy files into
  place atomically.
