# spark-md5 3.0.2

- Source: https://github.com/satazor/js-spark-md5 (npm `spark-md5@3.0.2`), copyright (C) 2015 André Cruz.
- Licence: the package is dual-licensed `(WTFPL OR MIT)` per its package.json. Advanced uses it under the **MIT**
  licence; the MIT notice is in [LICENSE](LICENSE).
- Vendored file: `wwwroot/lib/spark-md5/spark-md5.min.js`, unmodified, from
  https://cdn.jsdelivr.net/npm/spark-md5@3.0.2/spark-md5.min.js
  (SHA-256 `d80e84c820cc5587a0ba3c8a20652099ea3fa7fc43944e812e56d449c1d9f1c9`, identical to unpkg's copy).
- Used by `wwwroot/js/uploader.js` to compute the per-piece Content-MD5 (WebCrypto has no MD5).
