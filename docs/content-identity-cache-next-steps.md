# Content-identity cache — next steps

Status: **not built.** This is the design note for the follow-up to `LargeFileIdentity`
(spec §3.4.1), deferred deliberately because it is the only part of the idea that needs new durable
state.

## Why there is anything left to do

`LargeFileIdentity` made the §3.4.1 unchanged-check cheap by reading *less* of each file: above a size
threshold a duplicate is settled from ~8 MiB of sampled windows, or from size + mtime alone, instead of
two full reads. That is a large win and it applies on the very first encounter with a file.

It buys that win with a **probabilistic** verdict, though — exact when it says two files differ, a
judgement call when it says they are identical (spec §3.4.1, "What this trades away, precisely"). And
it still reads *something* per duplicate per run.

A cache attacks the same cost from the other side: **remember digests already computed** and skip the
read entirely when nothing about the file has changed. Where the sampled digest trades certainty for
speed, the cache trades disk space for speed and gives up much less certainty — for anything it has
seen before, its answer is derived from a hash it computed itself over the whole file.

The two compose cleanly and should both exist:

```
cache hit  -> 0 bytes read          (exact, if the file is genuinely unchanged)
cache miss -> sampled read (~8 MiB) (or a full read under FullHash)
```

The headline case is the one users actually hit: a media library synced on a schedule. Today, and even
after `LargeFileIdentity`, run *N+1* over an unchanged 500 GB library still touches every file. With a
cache it becomes a metadata walk.

## Shape

A store keyed by file identity, mapping to the digest we last computed for it:

```
key:   (volume identity, normalized path, size, last-write-time, algorithm/layout)
value: digest
```

`algorithm/layout` is part of the key, not the value — a full XXH3-128 digest, a SHA-256 digest and a
sampled digest under `SampledHashLayout` version 1 are three different values over three different
byte sets, and the existing domain-separation prefix already guarantees they can never be confused.
Reusing one where another is wanted must be a miss, not a coincidence.

Write points (all already compute a digest, so none adds I/O):

| Site | What it learns |
|---|---|
| `JobExecutor.ProbeIdentityAsync` | the incoming file's digest |
| `AtomicPlacer.CheckUnchangedAsync` | an existing destination's digest |
| `AtomicPlacer.VerifyAsync` | the digest of a file we just wrote — the highest-confidence entry available, since we produced those bytes |
| `DryRunEngine.EvaluateTargetAsync` | both sides, during the preview that usually precedes the run |

That last row is worth calling out: a preview immediately followed by a run currently pays for the
same reads twice. A cache shared between them removes one of the two passes with no change in
guarantees at all — arguably the single cheapest win in this whole area.

## Open questions

These are the reasons it is not built yet, roughly in order of how much they shape the design.

1. **Trust model.** A cache hit asserts "size and mtime unchanged ⇒ content unchanged", which is
   rsync's and robocopy's default assumption but is still an assumption. It is *stronger* than
   `SizeAndTimestamp` (the digest behind it covered the whole file) and weaker than re-reading. Does a
   hit satisfy `LargeFileIdentity: FullHash`, or should `FullHash` mean "actually read it, every
   time"? If the latter, the cache only ever accelerates the cheaper tiers and the media-library case
   is unaffected — which would undercut most of its value. Needs a decision before anything else.

2. **Where it lives, and what it is.** Options, from least to most machinery:
   - an in-memory cache for the life of one run (kills the preview→run double read, survives nothing);
   - an append-only NDJSON log beside the journal, compacted on load (matches existing
     infrastructure — `JobJournal`, `RunSnapshotStore` — and the AOT/`System.Text.Json`
     source-generated conventions, so no new dependency);
   - an embedded key/value store (fastest lookups, first third-party storage dependency in the repo;
     note there is currently no SQLite/LiteDB reference anywhere and `PublishAot=true` on Service and
     UI, so this needs care).
   The NDJSON option is the one that fits the codebase as it stands.

3. **Volume identity.** Keying on a path string alone is wrong across drive-letter reassignment,
   removable media and network shares. `NormalizedPath` handles case/separator normalization but not
   "is this the same physical volume". `IVolumeInfoProvider` is the place to look; a stale key that
   silently matches the wrong volume would be a correctness bug of exactly the kind this feature is
   supposed to avoid.

4. **Bounds and eviction.** Unbounded growth is not acceptable for a library-sized workload. Size cap,
   LRU, or age-based expiry — and whichever it is, the run report should be honest about it rather than
   letting a silently-evicted entry look like a cold start.

5. **Corruption and forward compatibility.** Follow `SettingsService.PreserveUnreadable`: an
   unreadable store is renamed `.corrupt-<timestamp>` and the run continues from empty. A cache must
   never be able to fail a job — every miss is just a read.

6. **Invalidation on our own writes.** After placing a file we know its new size, mtime and digest, so
   the entry should be *updated* rather than invalidated. `SelfWriteSuppressionRegistry` already tracks
   which paths are ours; the interaction wants checking.

## Related

- Spec §3.4.1 (`LargeFileIdentity`, the shipped bounded-read tier) and the `IdentityStrategy` /
  `SampledHashLayout` sections of `docs/specs/architecture-v1.md`.
- `Filters.ContentHashDedupe` — reserved, and blocked on a *target index*
  (spec Appendix B). That index is a near-neighbour of this store: content-addressed rather than
  path-addressed, but with the same durability, bounding and invalidation questions. Worth designing
  the two together rather than growing two stores.
- `docs/mirror-run-next-steps.md` §"Duplicate source election" — notes that two sources holding the
  same file are hashed independently today because "the hashes exist, they are simply not shared
  between the two jobs". A cache is one way to share them.
