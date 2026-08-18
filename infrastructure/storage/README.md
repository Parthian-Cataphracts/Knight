# Object Storage

Local development uses the filesystem-backed `IObjectStorage` implementation in
`Knight.Infrastructure` (see `Storage:LocalRootPath` configuration). Production
environments must configure an S3-compatible provider instead; no code outside
of `Knight.Infrastructure/Storage` should assume a local filesystem layout.
