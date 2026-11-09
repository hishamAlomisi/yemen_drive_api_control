# Pinned administration browser dependencies

- `@microsoft/signalr` **9.0.19**, MIT: browser bundle and source map from the official npm package. The existing complete license notice is retained in `signalr/THIRD-PARTY-NOTICE.txt`.
- `leaflet` **1.9.4**, BSD-2-Clause: JavaScript, CSS, source map, all five images, and `LICENSE` from the official npm package.

`vendor-lock.json` records the exact official package URLs, SHA512 package integrity values and SHA256 hashes of the checked-in files. Download the pinned package, verify its integrity before extracting it, and update this manifest when deliberately changing versions. Keep the licenses with the bundles.

These files must remain inside `src/YemenDrive.Api/wwwroot/vendor`. The repository-level `wwwroot` directory is outside the API project and is not published by the Web SDK. `ValidateAdminVendorAssets` checks required runtime files and licenses before building or publishing; vendor content is explicitly copied to output and publication. The packaging smoke check must also verify the local URLs referenced by the HTML and CSS.
