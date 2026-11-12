# sut-rules measurements (2026-11-12 17:54 UTC)

- OS: Microsoft Windows 10.0.26200; X64
- .NET: .NET 10.0.12; logical CPUs: 16; server GC: False
- Batch: 20,000 invoices, 54,100 lines (2.71 per invoice); 15 measured runs after 3 warm-up runs

## Compile

| Step | Median ms | Min ms | Max ms |
|---|---:|---:|---:|
| First compile in the process (JIT included) | 122.6 | - | - |
| Compile 22 rules + catalog from memory (warm) | 3.06 | 1.53 | 18.30 |
| Load from disk: read, hash, compile (warm) | 4.90 | 3.67 | 8.66 |

## Throughput

| Path | Median inv/s | Min inv/s | Max inv/s | Median ms per batch | Allocated per invoice |
|---|---:|---:|---:|---:|---:|
| Rule engine, sequential | 61,537 | 38,981 | 142,862 | 325.0 | 4,367 B |
| Rule engine, parallel (PLINQ) | 91,748 | 44,553 | 143,648 | 218.0 | n/a (other threads) |

## Cost per rule (2,000 invoices, one rule enabled at a time, median of 9, minus the no-rule baseline)

No-rule baseline: 2.14 ms per 2,000 invoices.

| Rule | Net median ms | Share of the sum |
|---|---:|---:|
| `DEM-003` | 8.60 | 19.9 % |
| `FRQ-002` | 8.35 | 19.3 % |
| `FRQ-001` | 7.56 | 17.5 % |
| `DEM-002` | 4.87 | 11.3 % |
| `MDL-006` | 2.38 | 5.5 % |
| `PKG-001` | 2.02 | 4.7 % |
| `MDL-005` | 1.51 | 3.5 % |
| `DEM-001` | 1.33 | 3.1 % |
| `PKG-002` | 1.30 | 3.0 % |
| `CLN-002` | 1.07 | 2.5 % |
| `MDL-002` | 0.92 | 2.1 % |
| `PRC-002` | 0.70 | 1.6 % |
| `ADM-001` | 0.61 | 1.4 % |
| `MDL-003` | 0.57 | 1.3 % |
| `MDL-001` | 0.53 | 1.2 % |
| `PKG-003` | 0.36 | 0.8 % |
| `CLN-001` | 0.32 | 0.7 % |
| `MDL-007` | 0.19 | 0.4 % |
| `MDL-004` | 0.05 | 0.1 % |
| `PKG-004` | 0.00 | 0.0 % |
| `PRC-001` | 0.00 | 0.0 % |
| `PRV-001` | 0.00 | 0.0 % |

## Golden suite

- 108 cases: median 16.6 ms (min 11.5, max 28.5), including reading the case files
