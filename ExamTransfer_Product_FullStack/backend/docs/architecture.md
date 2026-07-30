# Backend architecture

The backend is a local-first modular monolith hosted on the teacher machine.

- `Shared.Contracts`: the only DTO/enum/event package shared with frontend.
- `Domain`: entities and state machines.
- `Application`: use-case interfaces and boundary abstractions.
- `Infrastructure`: SQLite/EF Core, local files, chunk assembly, receipts, backup, Supabase adapter, service implementations.
- `LocalServer`: REST, SignalR, authentication, discovery, middleware, and background workers.
- `Agent`: protocol-compatible baseline for the advanced exam-control module; it does not yet modify Windows.

Active LAN sessions never require Supabase. Outbox records are synchronized later by `CloudSyncWorker`.
