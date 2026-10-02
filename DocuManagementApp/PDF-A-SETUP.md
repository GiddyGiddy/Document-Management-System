# PDF/A conversion setup

The API creates PDF/A-2b with Ghostscript in a Podman/Docker container and independently validates the result with the veraPDF REST service. It does not claim PDF/A conformance if either tool is unavailable, conversion fails, or veraPDF does not report a compliant result.

Word, Excel, RTF, and OpenDocument inputs are converted to PDF with LibreOffice. The service prefers an installed LibreOffice executable and automatically falls back to the configured LibreOffice container when none is found.

Configure these values in user secrets or environment variables for the application process:

| Configuration key | Required value |
| --- | --- |
| `PdfA:ContainerRuntimeExecutablePath` | `podman.exe` on Windows or `podman` on Linux; Docker can also be used |
| `PdfA:GhostscriptImage` | Ghostscript container image, default `docker.io/minidocks/ghostscript:latest` |
| `PdfA:VeraPdfRestBaseUrl` | Base URL of the running veraPDF REST container, default `http://localhost:8080` |
| `PdfA:TimeoutSeconds` | Optional per-process timeout; defaults to 120 seconds |
| `LibreOffice:ExecutablePath` | Optional path to a host `soffice` executable; empty means use a detected default or the container fallback |
| `LibreOffice:ContainerRuntimeExecutablePath` | Optional Podman/Docker command; defaults to `podman.exe` on Windows or `podman` on Linux |
| `LibreOffice:ContainerImage` | LibreOffice image, default `docker.io/linuxserver/libreoffice:latest` |

Start the REST validator with the `verapdf/rest` image and ensure it is reachable from the ASP.NET process. The Ghostscript image used by the default configuration includes `/usr/share/ghostscript/iccprofiles/srgb.icc`.

The LibreOffice container needs the input file mounted read-only and the output directory mounted writable. Pull the configured image before running conversions if the container runtime cannot access its registry.

Conversion uses Ghostscript's PDF/A-2 mode, embeds the bundled RGB output intent, writes to a temporary candidate, and only publishes the requested output path after veraPDF confirms PDF/A-2b compliance. `/api/pdfa/validate` posts the PDF to the veraPDF REST API for validation.