# Acta Bench — INCONCLUSIVE

Protocol: `acta-bench.full/1`  
Build: `fc5fbf1aa436013fbcb26b8bae1846cebe921efa` (source dirty: False)  
Run: `20261019T044452329Z-eef32bb0d6bf498eb49bede6f79a21c9`

Requested providers: sqlite, pg, mssql  
Completed providers: mssql  
Durability: single_node_durable; settings verified: True

Case instances: safety failures 0; liveness failures 0; coverage gaps 3. Fault-case instances covered: 60 of 60.

Requested profile: **standard**, 61 of 61 engine cases. A selected-case or quick PASS establishes only that declared scope.

Standard uses one conservative arrival rate, one measured trial, reduced populations and fault repetitions. It does not establish maximum capacity, repeated-trial confidence, or extended endurance.

Capacity measurement is informational in the standard profile: safety, liveness, correctness coverage, and the ledger checks decide each case and the verdict, and discovery, measured trials, and the E03 overload check are only reported.

compilation_mode: managed_jit  
worker_compilation_mode: managed_jit  
cpu_model: Intel(R) Core(TM) i9-14900K  
physical_cores: 24  
processor_count: 32  
memory_available_to_runtime_bytes: 102818476032  
filesystem: ReFS  

| Case | Provider | Phase | Safety | Liveness | Coverage | Measurement | Status | Reason |
|---|---|---|---|---|---|---|---|---|
| H01 | harness | self_test | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Synthetic harness evidence; not an engine case. |
| H02 | harness | self_test | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Synthetic harness evidence; not an engine case. |
| H03 | harness | self_test | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Synthetic harness evidence; not an engine case. |
| H04 | harness | self_test | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Synthetic harness evidence; not an engine case. |
| H05 | harness | self_test | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Synthetic harness evidence; not an engine case. |
| H06 | harness | self_test | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Synthetic harness evidence; not an engine case. |
| H07 | harness | self_test | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Synthetic harness evidence; not an engine case. |
| H08 | harness | self_test | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Synthetic harness evidence; not an engine case. |
| L01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L01 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | sqlite | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| L01 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L02 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L02 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| L04 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L04 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| C01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C01 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| C02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C03 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C04 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C05 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P01 | sqlite | discover | PASS | PASS | COMPLETE | OBSERVER_LIMITED | PASS |  |
| P01 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| P01 | sqlite | discover | PASS | PASS | COMPLETE | OBSERVER_LIMITED | PASS |  |
| P01 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| P02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P03 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P04 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P05 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P06 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P07 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P08 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T01 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T03 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T04 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T05 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T06 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R01 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | sqlite | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| R01 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| R02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R03 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | sqlite | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| O01 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| O02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| O03 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O03 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| O03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| O04 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Finite strict-priority/tenant contention; no starvation-freedom claim. |
| O05 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F01 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F02 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F03 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F04 | sqlite | fault | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | SQLite uses embedded storage; this profile has no worker-to-database TCP connection. |
| F05 | sqlite | fault | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | SQLite is embedded; the profile has no separate database process. |
| F06 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F07 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F08 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | In-process wakeup accelerator disabled; durable safety polling completed the same native child/join workload. |
| F09 | sqlite | verify | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | INCONCLUSIVE | Case did not finish (TimeoutException: The operation has timed out.); partial histories retained. |
| S01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | sqlite | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| S02 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| S03 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S03 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | sqlite | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| S03 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| S04 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S05 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I03 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I04 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| M01 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M01 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | sqlite | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| M01 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| M02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | sqlite | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| M04 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M04 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | sqlite | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| M04 | sqlite | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| D01 | sqlite | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| D02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Versioned fixture modes share one immutable executable: v1 persisted a result, v2 adds an optional result field; incompatible mode changes the registered input type. These are declared fixture contracts, not historical release upgrades. |
| D03 | sqlite | verify | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Versioned fixture modes share one immutable executable: v1 persisted a result, v2 adds an optional result field; incompatible mode changes the registered input type. These are declared fixture contracts, not historical release upgrades. |
| D04 | sqlite | verify | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | This pre-1.0 tree publishes one baseline migration per provider and no supported predecessor upgrade pair. |
| E01 | sqlite | soak | PASS | PASS | COMPLETE | VALID | PASS |  |
| E02 | sqlite | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| E02 | sqlite | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | sqlite | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| E02 | sqlite | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E03 | sqlite | fault | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| F09 | sqlite | reconcile | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | INCONCLUSIVE | Required seeded busy kills, reclaim evidence, or acknowledged cohort accounting is incomplete. |
| E02 | sqlite | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| L01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | pg | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| L04 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L04 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| C01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| C01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | pg | discover | PASS | PASS | COMPLETE | OBSERVER_LIMITED | PASS |  |
| C01 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| C02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C03 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C04 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C05 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P01 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| P01 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| P01 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| P01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| P01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| P01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| P01 | pg | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| P02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P03 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P04 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P05 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P06 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P07 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P08 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T01 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T03 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T04 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T05 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T06 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| R02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R03 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| O01 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| O02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| O02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| O03 | pg | verify | UNVERIFIED | UNVERIFIED | INCOMPLETE | NOT_MEASURED | INCONCLUSIVE | Case did not finish (TimeoutException: Declared recovery bound elapsed.); partial histories retained. |
| O04 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Finite strict-priority/tenant contention; no starvation-freedom claim. |
| O05 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F01 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F02 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F03 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F04 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Black-box connectivity failure; no specific lost-transaction-reply boundary is inferred. |
| F05 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Controller probes retry transient post-crash reads; the worker process is not restarted by the harness. |
| F06 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F07 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F08 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | In-process wakeup accelerator disabled; durable safety polling completed the same native child/join workload. |
| F09 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| S03 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S03 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| S03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| S04 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S05 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I03 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I04 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| I05 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | pg | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | pg | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | pg | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | pg | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | pg | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M04 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| D01 | pg | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| D02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Versioned fixture modes share one immutable executable: v1 persisted a result, v2 adds an optional result field; incompatible mode changes the registered input type. These are declared fixture contracts, not historical release upgrades. |
| D03 | pg | verify | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Versioned fixture modes share one immutable executable: v1 persisted a result, v2 adds an optional result field; incompatible mode changes the registered input type. These are declared fixture contracts, not historical release upgrades. |
| D04 | pg | verify | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | This pre-1.0 tree publishes one baseline migration per provider and no supported predecessor upgrade pair. |
| E01 | pg | soak | PASS | PASS | COMPLETE | OBSERVER_LIMITED | PASS | Ran at M03's best qualifying discovery rate of 1600 roots/s: no M03 rate validated, and the standard profile does not gate its verdict on measurement. |
| E02 | pg | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| E02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | pg | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | pg | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| E03 | pg | fault | PASS | PASS | COMPLETE | VALID | PASS | Ran at L01's best qualifying discovery rate of 6400 roots/s: no L01 rate validated, and the standard profile does not gate its verdict on measurement. |
| L01 | pg | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| P01 | pg | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| O03 | pg | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | Capacity discovery did not find a valid arrival rate. Informational: capacity measurement does not decide the standard profile's verdict. |
| I05 | pg | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| M01 | pg | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| M02 | pg | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| M03 | pg | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| L01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| L01 | mssql | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| L01 | mssql | measure | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| L02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L02 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| L03 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| L04 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| L04 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| C01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C01 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| C01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C01 | mssql | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| C02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C03 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C04 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| C05 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| P01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| P01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| P01 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| P02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P03 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P04 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P05 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P06 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P07 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| P08 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T01 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T03 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T04 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T05 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| T06 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| R01 | mssql | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| R01 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| R02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| R03 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O01 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| O01 | mssql | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| O01 | mssql | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| O02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| O02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O02 | mssql | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O03 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| O03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| O03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O03 | mssql | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| O04 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Finite strict-priority/tenant contention; no starvation-freedom claim. |
| O05 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F01 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F02 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F03 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F04 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Black-box connectivity failure; no specific lost-transaction-reply boundary is inferred. |
| F05 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Controller probes retry transient post-crash reads; the worker process is not restarted by the harness. |
| F06 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F07 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| F08 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | In-process wakeup accelerator disabled; durable safety polling completed the same native child/join workload. |
| F09 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S02 | mssql | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| S02 | mssql | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S03 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| S03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| S03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| S03 | mssql | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| S03 | mssql | measure | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| S04 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| S05 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I03 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I04 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| I05 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| I05 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| I05 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| M01 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M01 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M01 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M01 | mssql | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| M01 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| M02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M02 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M03 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M03 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| M04 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| M04 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| M04 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| M04 | mssql | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| M04 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| D01 | mssql | fault | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| D02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS | Versioned fixture modes share one immutable executable: v1 persisted a result, v2 adds an optional result field; incompatible mode changes the registered input type. These are declared fixture contracts, not historical release upgrades. |
| D03 | mssql | verify | PASS | NOT_APPLICABLE | COMPLETE | NOT_MEASURED | PASS | Versioned fixture modes share one immutable executable: v1 persisted a result, v2 adds an optional result field; incompatible mode changes the registered input type. These are declared fixture contracts, not historical release upgrades. |
| D04 | mssql | verify | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | This pre-1.0 tree publishes one baseline migration per provider and no supported predecessor upgrade pair. |
| E01 | mssql | soak | PASS | PASS | COMPLETE | VALID | PASS |  |
| E02 | mssql | verify | PASS | PASS | COMPLETE | NOT_MEASURED | PASS |  |
| E02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | mssql | discover | PASS | PASS | COMPLETE | UNSTABLE | PASS |  |
| E02 | mssql | discover | PASS | PASS | COMPLETE | VALID | PASS |  |
| E02 | mssql | discover | PASS | PASS | COMPLETE | GENERATOR_LIMITED | PASS |  |
| E02 | mssql | measure | PASS | PASS | COMPLETE | VALID | PASS |  |
| E03 | mssql | fault | PASS | PASS | COMPLETE | VALID | PASS | Ran at L01's best qualifying discovery rate of 3200 roots/s: no L01 rate validated, and the standard profile does not gate its verdict on measurement. |
| L01 | mssql | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| C01 | mssql | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| O01 | mssql | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| O02 | mssql | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| O03 | mssql | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| S02 | mssql | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |
| S03 | mssql | reconcile | NOT_APPLICABLE | NOT_APPLICABLE | NOT_APPLICABLE | NOT_MEASURED | NOT_APPLICABLE | No arrival rate passed all required independent validation trials; correctness results remain separate. Informational: capacity measurement does not decide the standard profile's verdict. |

Capacity is qualified per workload and repetition. Missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.

| Workload | Provider | Cell | Offered roots/s | Trials | Valid trials | Median observed rate | Unit | Min-max | MAD (dispersion metric) | Validated |
|---|---|---|---:|---:|---:|---:|---|---|---:|---|
| L01 | sqlite | canonical | 25 | 1 | 1 | 25.4 | roots/second | 25.4-25.4 | 0 primary_latency_p99 | True |
| L02 | sqlite | canonical | 25 | 1 | 1 | 25 | admissions/second | 25-25 | 0 primary_latency_p99 | True |
| L03 | sqlite | canonical | 3200 | 1 | 1 | 3193.3 | admissions/second | 3193.3-3193.3 | 0 primary_latency_p99 | True |
| L04 | sqlite | canonical | 0 | 1 | 1 | 1996.9 | backlog_roots/second | 1996.9-1996.9 | 0 primary_rate | True |
| C01 | sqlite | canonical | 12 | 1 | 1 | 11.867 | roots/second | 11.867-11.867 | 0 primary_latency_p99 | True |
| P01 | sqlite | canonical | 12 | 1 | 1 | 12 | roots/second | 12-12 | 0 primary_latency_p99 | True |
| R01 | sqlite | canonical | 25 | 1 | 1 | 25.667 | roots/second | 25.667-25.667 | 0 primary_latency_p99 | True |
| O01 | sqlite | canonical | 25 | 1 | 1 | 26 | roots/second | 26-26 | 0 primary_latency_p99 | True |
| O02 | sqlite | canonical | 12 | 1 | 1 | 12 | roots/second | 12-12 | 0 primary_latency_p99 | True |
| O03 | sqlite | canonical | 1 | 1 | 1 | 1.0667 | roots/second | 1.0667-1.0667 | 0 primary_latency_p99 | True |
| S02 | sqlite | canonical | 25 | 1 | 1 | 23.8 | roots/second | 23.8-23.8 | 0 primary_latency_p99 | True |
| S03 | sqlite | canonical | 25 | 1 | 1 | 25.067 | roots/second | 25.067-25.067 | 0 primary_latency_p99 | True |
| I05 | sqlite | canonical | 6 | 1 | 1 | 6 | roots/second | 6-6 | 0 primary_latency_p99 | True |
| M01 | sqlite | canonical | 25 | 1 | 1 | 25.6 | roots/second | 25.6-25.6 | 0 primary_latency_p99 | True |
| M02 | sqlite | canonical | 6 | 1 | 1 | 5.9333 | roots/second | 5.9333-5.9333 | 0 primary_latency_p99 | True |
| M03 | sqlite | canonical | 12 | 1 | 1 | 11.8 | roots/second | 11.8-11.8 | 0 primary_latency_p99 | True |
| M04 | sqlite | canonical | 25 | 1 | 1 | 25.733 | roots/second | 25.733-25.733 | 0 primary_latency_p99 | True |
| E02 | sqlite | canonical | 25 | 1 | 0 | 24.867 | roots/second | 24.867-24.867 | 0 primary_latency_p99 | False |
| L01 | pg | canonical | 3200 | 1 | 0 | 3039.8 | roots/second | 3039.8-3039.8 | 0 primary_latency_p99 | False |
| L02 | pg | canonical | 3200 | 1 | 1 | 3197.7 | admissions/second | 3197.7-3197.7 | 0 primary_latency_p99 | True |
| L03 | pg | canonical | 3200 | 1 | 1 | 3200 | admissions/second | 3200-3200 | 0 primary_latency_p99 | True |
| L04 | pg | canonical | 0 | 1 | 1 | 1922.3 | backlog_roots/second | 1922.3-1922.3 | 0 primary_rate | True |
| C01 | pg | canonical | 1600 | 1 | 1 | 1565.2 | roots/second | 1565.2-1565.2 | 0 primary_latency_p99 | True |
| P01 | pg | canonical | 400 | 1 | 0 | 253.6 | roots/second | 253.6-253.6 | 0 primary_latency_p99 | False |
| R01 | pg | canonical | 3200 | 1 | 1 | 3204.9 | roots/second | 3204.9-3204.9 | 0 primary_latency_p99 | True |
| O01 | pg | canonical | 3200 | 1 | 1 | 3163.6 | roots/second | 3163.6-3163.6 | 0 primary_latency_p99 | True |
| O02 | pg | canonical | 100 | 1 | 1 | 97.733 | roots/second | 97.733-97.733 | 0 primary_latency_p99 | True |
| S02 | pg | canonical | 3200 | 1 | 1 | 3177.5 | roots/second | 3177.5-3177.5 | 0 primary_latency_p99 | True |
| S03 | pg | canonical | 3200 | 1 | 1 | 3112.1 | roots/second | 3112.1-3112.1 | 0 primary_latency_p99 | True |
| I05 | pg | canonical | 800 | 1 | 0 | 304.8 | roots/second | 304.8-304.8 | 0 primary_latency_p99 | False |
| M01 | pg | canonical | 3200 | 1 | 0 | 3100.1 | roots/second | 3100.1-3100.1 | 0 primary_latency_p99 | False |
| M02 | pg | canonical | 800 | 1 | 0 | 300.07 | roots/second | 300.07-300.07 | 0 primary_latency_p99 | False |
| M03 | pg | canonical | 800 | 1 | 0 | 797.6 | roots/second | 797.6-797.6 | 0 primary_latency_p99 | False |
| M04 | pg | canonical | 1600 | 1 | 1 | 1675.3 | roots/second | 1675.3-1675.3 | 0 primary_latency_p99 | True |
| E02 | pg | canonical | 3200 | 1 | 1 | 3341.9 | roots/second | 3341.9-3341.9 | 0 primary_latency_p99 | True |
| L01 | mssql | canonical | 1600 | 1 | 0 | 1653.1 | roots/second | 1653.1-1653.1 | 0 primary_latency_p99 | False |
| L02 | mssql | canonical | 3200 | 1 | 1 | 3199 | admissions/second | 3199-3199 | 0 primary_latency_p99 | True |
| L03 | mssql | canonical | 3200 | 1 | 1 | 3200 | admissions/second | 3200-3200 | 0 primary_latency_p99 | True |
| L04 | mssql | canonical | 0 | 1 | 1 | 1376.4 | backlog_roots/second | 1376.4-1376.4 | 0 primary_rate | True |
| C01 | mssql | canonical | 800 | 1 | 0 | 826.13 | roots/second | 826.13-826.13 | 0 primary_latency_p99 | False |
| P01 | mssql | canonical | 50 | 1 | 1 | 53.667 | roots/second | 53.667-53.667 | 0 primary_latency_p99 | True |
| R01 | mssql | canonical | 1600 | 1 | 1 | 1596.1 | roots/second | 1596.1-1596.1 | 0 primary_latency_p99 | True |
| O01 | mssql | canonical | 1600 | 1 | 0 | 1602.7 | roots/second | 1602.7-1602.7 | 0 primary_latency_p99 | False |
| O02 | mssql | canonical | 100 | 1 | 0 | 102.67 | roots/second | 102.67-102.67 | 0 primary_latency_p99 | False |
| O03 | mssql | canonical | 1 | 1 | 0 | 1 | roots/second | 1-1 | 0 primary_latency_p99 | False |
| S02 | mssql | canonical | 1600 | 1 | 0 | 1604.9 | roots/second | 1604.9-1604.9 | 0 primary_latency_p99 | False |
| S03 | mssql | canonical | 1600 | 1 | 0 | 1650.4 | roots/second | 1650.4-1650.4 | 0 primary_latency_p99 | False |
| I05 | mssql | canonical | 400 | 1 | 1 | 384.93 | roots/second | 384.93-384.93 | 0 primary_latency_p99 | True |
| M01 | mssql | canonical | 800 | 1 | 1 | 813.4 | roots/second | 813.4-813.4 | 0 primary_latency_p99 | True |
| M02 | mssql | canonical | 200 | 1 | 1 | 204.07 | roots/second | 204.07-204.07 | 0 primary_latency_p99 | True |
| M03 | mssql | canonical | 50 | 1 | 1 | 49.867 | roots/second | 49.867-49.867 | 0 primary_latency_p99 | True |
| M04 | mssql | canonical | 800 | 1 | 1 | 792 | roots/second | 792-792 | 0 primary_latency_p99 | True |
| E02 | mssql | canonical | 1600 | 1 | 1 | 1628.3 | roots/second | 1628.3-1628.3 | 0 primary_latency_p99 | True |

MAD is the median absolute deviation, across independent trials, of the dispersion metric named beside it. Latency distributions remain per trial; percentiles are never averaged.

| Workload | Provider | Trial | Offered roots/s | Observed roots/s | p99 ms | Backlog slope | Measurement |
|---|---|---:|---:|---:|---:|---:|---|
| L01 | sqlite | 0 | 50 | 40.2 | 1131.2 | 1.468 | UNSTABLE |
| L01 | sqlite | 1 | 100 | 76.8 | 4500.2 | 5.474 | GENERATOR_LIMITED |
| L01 | sqlite | 1 | 25 | 25.4 | 1125.1 | 0.013144 | VALID |
| L02 | sqlite | 0 | 50 | 0 | 33.666 | 50.007 | VALID |
| L02 | sqlite | 1 | 100 | 0 | 3888.1 | 95.477 | UNSTABLE |
| L02 | sqlite | 1 | 25 | 0 | 29.563 | 25 | VALID |
| L03 | sqlite | 0 | 50 | 50 | 32.661 | 40.494 | VALID |
| L03 | sqlite | 1 | 100 | 100 | 26.745 | 100.27 | VALID |
| L03 | sqlite | 2 | 200 | 179.2 | 36.119 | 179.6 | VALID |
| L03 | sqlite | 3 | 400 | 0 | 49.184 | 399.34 | VALID |
| L03 | sqlite | 4 | 800 | 0 | 29.087 | 800.23 | VALID |
| L03 | sqlite | 5 | 1600 | 0 | 36.184 | 1600.2 | VALID |
| L03 | sqlite | 6 | 3200 | 0 | 29.087 | 3197.3 | VALID |
| L03 | sqlite | 7 | 6400 | 0 | 35.157 | 6405.1 | VALID |
| L03 | sqlite | 1 | 3200 | 0 | 32.945 | 3199.2 | VALID |
| L04 | sqlite | 1 | unavailable | 66.667 | 551.08 | unavailable | VALID |
| C01 | sqlite | 0 | 50 | 32.8 | 6574.4 | 8.5285 | UNSTABLE |
| C01 | sqlite | 1 | 25 | 17.4 | 3804.8 | 5.6807 | UNSTABLE |
| C01 | sqlite | 2 | 50 | 29 | 6364.3 | 8.8849 | UNSTABLE |
| C01 | sqlite | 1 | 12 | 11.867 | 1443.3 | 0.010176 | VALID |
| P01 | sqlite | 0 | 50 | 22.8 | 8480 | 22.683 | OBSERVER_LIMITED |
| P01 | sqlite | 1 | 25 | 4.2 | 7053.9 | 19.343 | UNSTABLE |
| P01 | sqlite | 2 | 50 | 13.2 | 9398.9 | 30.812 | OBSERVER_LIMITED |
| P01 | sqlite | 1 | 12 | 12 | 2019.1 | 0.024546 | VALID |
| R01 | sqlite | 0 | 50 | 39 | 3063.8 | 5.4165 | UNSTABLE |
| R01 | sqlite | 1 | 100 | 0 | 6865.5 | 93.248 | GENERATOR_LIMITED |
| R01 | sqlite | 1 | 25 | 25.667 | 3199.4 | -0.27582 | VALID |
| O01 | sqlite | 0 | 50 | 40 | 1112.9 | 0.82062 | UNSTABLE |
| O01 | sqlite | 1 | 100 | 54 | 4725 | 43.354 | GENERATOR_LIMITED |
| O01 | sqlite | 1 | 25 | 26 | 1125.1 | 0.027903 | VALID |
| O02 | sqlite | 0 | 50 | 10.8 | 5322.8 | 36.112 | UNSTABLE |
| O02 | sqlite | 1 | 25 | 21.8 | 1100.9 | 2.0236 | UNSTABLE |
| O02 | sqlite | 2 | 50 | 11 | 5649.5 | 35.545 | UNSTABLE |
| O02 | sqlite | 1 | 12 | 12 | 1088.3 | 0.023387 | VALID |
| O03 | sqlite | 0 | 50 | 2 | 8298.2 | 43.147 | UNSTABLE |
| O03 | sqlite | 1 | 25 | 15.4 | 6194.3 | 8.9976 | UNSTABLE |
| O03 | sqlite | 2 | 12 | 10.8 | 3187.1 | -0.1828 | UNSTABLE |
| O03 | sqlite | 3 | 6 | 4.6 | 3110.9 | 0.61909 | UNSTABLE |
| O03 | sqlite | 4 | 3 | 2.6 | 3077.7 | -0.34694 | UNSTABLE |
| O03 | sqlite | 5 | 1 | 0.8 | 1014.5 | 0.0095772 | VALID |
| O03 | sqlite | 6 | 2 | 1.8 | 3078.8 | -0.218 | UNSTABLE |
| O03 | sqlite | 1 | 1 | 1.0667 | 1035.4 | -0.0045399 | VALID |
| S02 | sqlite | 0 | 50 | 41.8 | 1143.5 | 1.1214 | UNSTABLE |
| S02 | sqlite | 1 | 100 | 66.6 | 5528.4 | 11.89 | GENERATOR_LIMITED |
| S02 | sqlite | 1 | 25 | 23.8 | 1137.3 | -0.043588 | VALID |
| S03 | sqlite | 0 | 50 | 40.4 | 1149.7 | 1.0715 | UNSTABLE |
| S03 | sqlite | 1 | 100 | 14.8 | 5351.7 | 80.596 | GENERATOR_LIMITED |
| S03 | sqlite | 1 | 25 | 25.067 | 1137.3 | 0.029161 | VALID |
| I05 | sqlite | 0 | 50 | 27.2 | 7286.9 | 9.3794 | UNSTABLE |
| I05 | sqlite | 1 | 25 | 17.2 | 3763.8 | 6.0602 | UNSTABLE |
| I05 | sqlite | 2 | 12 | 10.4 | 1608.8 | 0.70166 | UNSTABLE |
| I05 | sqlite | 3 | 24 | 16 | 3287.2 | 5.418 | UNSTABLE |
| I05 | sqlite | 1 | 6 | 6 | 1285.6 | 0.012468 | VALID |
| M01 | sqlite | 0 | 50 | 40.6 | 1155.9 | 1.1261 | UNSTABLE |
| M01 | sqlite | 1 | 100 | 73.6 | 4309.4 | 16.887 | GENERATOR_LIMITED |
| M01 | sqlite | 1 | 25 | 25.6 | 1137.3 | -0.057189 | VALID |
| M02 | sqlite | 0 | 50 | 14 | 6503.6 | 26.192 | UNSTABLE |
| M02 | sqlite | 1 | 25 | 17 | 3784.2 | 7.0022 | UNSTABLE |
| M02 | sqlite | 2 | 12 | 9.8 | 1513.8 | 0.64287 | UNSTABLE |
| M02 | sqlite | 3 | 24 | 16 | 3341.1 | 5.0208 | UNSTABLE |
| M02 | sqlite | 1 | 6 | 5.9333 | 1322.3 | 0.0066678 | VALID |
| M03 | sqlite | 0 | 50 | 7.4 | 7486.9 | 37.313 | UNSTABLE |
| M03 | sqlite | 1 | 25 | 17.4 | 5097.1 | 5.6942 | UNSTABLE |
| M03 | sqlite | 2 | 12 | 10.8 | 2259.4 | -0.082391 | VALID |
| M03 | sqlite | 3 | 24 | 19.8 | 3014.4 | 2.206 | UNSTABLE |
| M03 | sqlite | 4 | 48 | 8.2 | 6574.4 | 37.93 | UNSTABLE |
| M03 | sqlite | 1 | 12 | 11.8 | 2213.8 | 0.059776 | VALID |
| M04 | sqlite | 0 | 50 | 40.6 | 1162.2 | 1.4516 | UNSTABLE |
| M04 | sqlite | 1 | 100 | 39 | 4802.4 | 42.623 | GENERATOR_LIMITED |
| M04 | sqlite | 1 | 25 | 25.733 | 1137.3 | 0.040037 | VALID |
| E01 | sqlite | 1 | 8 | 7.9 | 2213.8 | -0.0038127 | VALID |
| E02 | sqlite | 0 | 50 | 40.6 | 1125.1 | 0.9769 | UNSTABLE |
| E02 | sqlite | 1 | 100 | 71.4 | 5152.6 | 3.7768 | GENERATOR_LIMITED |
| E02 | sqlite | 1 | 25 | 24.867 | 1125.1 | 0.12955 | UNSTABLE |
| L01 | pg | 0 | 50 | 40.4 | 1106.9 | 0.75538 | UNSTABLE |
| L01 | pg | 1 | 100 | 80.4 | 1095 | 1.9107 | UNSTABLE |
| L01 | pg | 2 | 200 | 160.4 | 1031.7 | 1.956 | UNSTABLE |
| L01 | pg | 3 | 400 | 327 | 1048.6 | -0.35366 | VALID |
| L01 | pg | 4 | 800 | 663.6 | 1048.6 | 10.217 | UNSTABLE |
| L01 | pg | 5 | 1600 | 1566.8 | 1065.7 | 122.26 | UNSTABLE |
| L01 | pg | 6 | 3200 | 2960.6 | 1048.6 | 137.33 | UNSTABLE |
| L01 | pg | 7 | 6400 | 5524 | 1083.2 | 27.282 | UNSTABLE |
| L01 | pg | 1 | 3200 | 3039.8 | 1071.5 | 9.9239 | UNSTABLE |
| L02 | pg | 0 | 50 | 0 | 23.935 | 49.998 | VALID |
| L02 | pg | 1 | 100 | 0 | 22.92 | 99.979 | VALID |
| L02 | pg | 2 | 200 | 0 | 23.677 | 199.98 | VALID |
| L02 | pg | 3 | 400 | 0 | 22.429 | 399.99 | VALID |
| L02 | pg | 4 | 800 | 0 | 25.13 | 799.95 | VALID |
| L02 | pg | 5 | 1600 | 0 | 28.463 | 1600.6 | VALID |
| L02 | pg | 6 | 3200 | 0 | 26.385 | 3199.1 | VALID |
| L02 | pg | 7 | 6400 | 0 | 22.429 | 6400.4 | VALID |
| L02 | pg | 1 | 3200 | 0 | 21.594 | 3199.9 | VALID |
| L03 | pg | 0 | 50 | 50 | 26.62 | 40.926 | VALID |
| L03 | pg | 1 | 100 | 100 | 26.169 | 100.71 | VALID |
| L03 | pg | 2 | 200 | 106.6 | 37.132 | 196.91 | VALID |
| L03 | pg | 3 | 400 | 0 | 45.33 | 402.66 | VALID |
| L03 | pg | 4 | 800 | 0 | 33.639 | 801.5 | VALID |
| L03 | pg | 5 | 1600 | 0 | 30.986 | 1600.4 | VALID |
| L03 | pg | 6 | 3200 | 0 | 30.705 | 3203.1 | VALID |
| L03 | pg | 7 | 6400 | 0 | 24.327 | 6396.4 | VALID |
| L03 | pg | 1 | 3200 | 0 | 34.218 | 3193.9 | VALID |
| L04 | pg | 1 | unavailable | 66.667 | 574.14 | unavailable | VALID |
| C01 | pg | 0 | 50 | 40.8 | 1125.1 | 1.3824 | UNSTABLE |
| C01 | pg | 1 | 100 | 83 | 1119 | 2.3113 | UNSTABLE |
| C01 | pg | 2 | 200 | 173 | 1089.1 | 2.8453 | UNSTABLE |
| C01 | pg | 3 | 400 | 383.8 | 1106.9 | 18.277 | UNSTABLE |
| C01 | pg | 4 | 800 | 671.4 | 1106.9 | 2.8778 | UNSTABLE |
| C01 | pg | 5 | 1600 | 1459.6 | 1100.9 | -40.056 | VALID |
| C01 | pg | 6 | 3200 | 1883 | 2178.2 | 1104.9 | UNSTABLE |
| C01 | pg | 7 | 6400 | 1481 | 7208.4 | 6000.2 | OBSERVER_LIMITED |
| C01 | pg | 1 | 1600 | 1565.2 | 1106.9 | -5.1192 | VALID |
| P01 | pg | 0 | 50 | 48.2 | 1162.2 | -4.4432 | VALID |
| P01 | pg | 1 | 100 | 97.2 | 1260.6 | -11.034 | VALID |
| P01 | pg | 2 | 200 | 193.6 | 1591.1 | -32.289 | VALID |
| P01 | pg | 3 | 400 | 239.4 | 2274.6 | 155.3 | UNSTABLE |
| P01 | pg | 4 | 800 | 165.8 | 6061.5 | 645.58 | UNSTABLE |
| P01 | pg | 5 | 1600 | 0 | 17904 | 1600.4 | UNSTABLE |
| P01 | pg | 1 | 400 | 253.6 | 10938 | 145.05 | UNSTABLE |
| R01 | pg | 0 | 50 | 43.4 | 3147.9 | 3.6523 | UNSTABLE |
| R01 | pg | 1 | 100 | 87.6 | 3030.8 | 4.7925 | UNSTABLE |
| R01 | pg | 2 | 200 | 174.2 | 3014.4 | 11.802 | UNSTABLE |
| R01 | pg | 3 | 400 | 347.2 | 2918 | 24.322 | UNSTABLE |
| R01 | pg | 4 | 800 | 695.4 | 2871 | 44.524 | UNSTABLE |
| R01 | pg | 5 | 1600 | 1409 | 2902.3 | 87.395 | UNSTABLE |
| R01 | pg | 6 | 3200 | 2786 | 2981.9 | 159.22 | UNSTABLE |
| R01 | pg | 7 | 6400 | 3968 | 4549.2 | 2007.9 | UNSTABLE |
| R01 | pg | 1 | 3200 | 3204.9 | 2262.3 | 0.49941 | VALID |
| O01 | pg | 0 | 50 | 40 | 1100.9 | 0.65329 | UNSTABLE |
| O01 | pg | 1 | 100 | 84.2 | 1089.1 | 3.227 | UNSTABLE |
| O01 | pg | 2 | 200 | 161.2 | 1042.9 | 0.75561 | UNSTABLE |
| O01 | pg | 3 | 400 | 326.8 | 1037.3 | 6.4476 | UNSTABLE |
| O01 | pg | 4 | 800 | 669.6 | 1054.3 | 6.845 | UNSTABLE |
| O01 | pg | 5 | 1600 | 1449.8 | 1060 | 25.819 | UNSTABLE |
| O01 | pg | 6 | 3200 | 2819.8 | 1054.3 | 21.288 | UNSTABLE |
| O01 | pg | 7 | 6400 | 6084.6 | 1065.7 | -341.25 | VALID |
| O01 | pg | 1 | 3200 | 3163.6 | 1065.7 | -9.4604 | VALID |
| O02 | pg | 0 | 50 | 49.8 | 1036.7 | 1.8638 | UNSTABLE |
| O02 | pg | 1 | 100 | 96.6 | 1042.9 | 1.5258 | UNSTABLE |
| O02 | pg | 2 | 200 | 186.6 | 1054.3 | -23.865 | VALID |
| O02 | pg | 3 | 400 | 166.8 | 4194.3 | 214.63 | UNSTABLE |
| O02 | pg | 1 | 100 | 97.733 | 1031.7 | -0.62753 | VALID |
| S02 | pg | 0 | 50 | 49.4 | 1100.9 | 1.2729 | UNSTABLE |
| S02 | pg | 1 | 100 | 97.8 | 1106.9 | 3.7756 | UNSTABLE |
| S02 | pg | 2 | 200 | 184.4 | 1065.7 | 5.5922 | UNSTABLE |
| S02 | pg | 3 | 400 | 329.4 | 1060 | 1.3822 | UNSTABLE |
| S02 | pg | 4 | 800 | 661.4 | 1048.6 | 12.104 | UNSTABLE |
| S02 | pg | 5 | 1600 | 1372.6 | 1054.3 | 29.086 | UNSTABLE |
| S02 | pg | 6 | 3200 | 3012 | 1071.5 | 125.73 | UNSTABLE |
| S02 | pg | 7 | 6400 | 5789.2 | 1089.1 | 11.663 | UNSTABLE |
| S02 | pg | 1 | 3200 | 3177.5 | 1060 | -10.438 | VALID |
| S03 | pg | 0 | 50 | 39.8 | 1100.9 | 0.7218 | UNSTABLE |
| S03 | pg | 1 | 100 | 79.8 | 1095 | 0.83415 | UNSTABLE |
| S03 | pg | 2 | 200 | 162 | 1060 | 2.1668 | UNSTABLE |
| S03 | pg | 3 | 400 | 323.2 | 1037.3 | -0.97822 | VALID |
| S03 | pg | 4 | 800 | 666.4 | 1048.6 | 9.4029 | UNSTABLE |
| S03 | pg | 5 | 1600 | 1405.2 | 1065.7 | 29.082 | UNSTABLE |
| S03 | pg | 6 | 3200 | 2911.6 | 1065.7 | 173.33 | UNSTABLE |
| S03 | pg | 7 | 6400 | 5533.6 | 1095 | 27.17 | UNSTABLE |
| S03 | pg | 1 | 3200 | 3112.1 | 1060 | 0.45302 | VALID |
| I05 | pg | 0 | 50 | 46.8 | 1137.3 | 3.9368 | UNSTABLE |
| I05 | pg | 1 | 100 | 85 | 1125.1 | 2.6697 | UNSTABLE |
| I05 | pg | 2 | 200 | 184.4 | 1131.2 | 5.7381 | UNSTABLE |
| I05 | pg | 3 | 400 | 333 | 1131.2 | 3.793 | UNSTABLE |
| I05 | pg | 4 | 800 | 731.2 | 1131.2 | -16.87 | VALID |
| I05 | pg | 5 | 1600 | 1087.6 | 1670.5 | 407.29 | UNSTABLE |
| I05 | pg | 6 | 3200 | 1020.6 | 6902.8 | 2073.4 | UNSTABLE |
| I05 | pg | 1 | 800 | 304.8 | 27912 | 496.22 | UNSTABLE |
| M01 | pg | 0 | 50 | 40.6 | 1125.1 | 0.63657 | UNSTABLE |
| M01 | pg | 1 | 100 | 81.6 | 1112.9 | 1.9391 | UNSTABLE |
| M01 | pg | 2 | 200 | 178.4 | 1083.2 | -2.6557 | VALID |
| M01 | pg | 3 | 400 | 345.8 | 1060 | 5.1382 | UNSTABLE |
| M01 | pg | 4 | 800 | 752.8 | 1083.2 | 30.026 | UNSTABLE |
| M01 | pg | 5 | 1600 | 1405.6 | 1089.1 | 5.6849 | UNSTABLE |
| M01 | pg | 6 | 3200 | 2814.2 | 1077.4 | 167.97 | UNSTABLE |
| M01 | pg | 7 | 6400 | 4213.2 | 1763.5 | 1799.9 | UNSTABLE |
| M01 | pg | 1 | 3200 | 3100.1 | 1095 | 13.348 | UNSTABLE |
| M02 | pg | 0 | 50 | 41 | 1143.5 | 0.88543 | UNSTABLE |
| M02 | pg | 1 | 100 | 85.4 | 1143.5 | 1.4809 | UNSTABLE |
| M02 | pg | 2 | 200 | 184.6 | 1137.3 | 6.7845 | UNSTABLE |
| M02 | pg | 3 | 400 | 335 | 1137.3 | 1.3722 | UNSTABLE |
| M02 | pg | 4 | 800 | 729.6 | 1137.3 | -14.05 | VALID |
| M02 | pg | 5 | 1600 | 1077.2 | 1697.9 | 415.07 | UNSTABLE |
| M02 | pg | 6 | 3200 | 952.8 | 12123 | 2111.2 | UNSTABLE |
| M02 | pg | 1 | 800 | 300.07 | 20835 | 501.13 | UNSTABLE |
| M03 | pg | 0 | 50 | 46.4 | 1773.1 | -3.0297 | VALID |
| M03 | pg | 1 | 100 | 93.4 | 1754 | -5.5952 | VALID |
| M03 | pg | 2 | 200 | 187.4 | 1782.7 | -11.261 | VALID |
| M03 | pg | 3 | 400 | 373.4 | 1871.7 | -26.936 | VALID |
| M03 | pg | 4 | 800 | 569.8 | 2576.3 | 194.04 | UNSTABLE |
| M03 | pg | 5 | 1600 | 1090.2 | 3047.2 | 424.17 | UNSTABLE |
| M03 | pg | 6 | 3200 | 1133.8 | 7208.4 | 1924.6 | UNSTABLE |
| M03 | pg | 1 | 800 | 797.6 | 1143.5 | 0.89718 | UNSTABLE |
| M04 | pg | 0 | 50 | 40.4 | 1106.9 | 1.0143 | UNSTABLE |
| M04 | pg | 1 | 100 | 81.2 | 1112.9 | 2.2389 | UNSTABLE |
| M04 | pg | 2 | 200 | 164.4 | 1060 | 1.7336 | UNSTABLE |
| M04 | pg | 3 | 400 | 345.4 | 1077.4 | 5.8078 | UNSTABLE |
| M04 | pg | 4 | 800 | 720 | 1065.7 | 6.7275 | UNSTABLE |
| M04 | pg | 5 | 1600 | 1267.8 | 1071.5 | 58.522 | UNSTABLE |
| M04 | pg | 6 | 3200 | 2744 | 1106.9 | 107.8 | UNSTABLE |
| M04 | pg | 7 | 6400 | 1894 | 3643.5 | 4515.5 | UNSTABLE |
| M04 | pg | 1 | 1600 | 1675.3 | 1071.5 | 0.11104 | VALID |
| E01 | pg | 1 | 1120 | 808.72 | 33014 | 342.99 | OBSERVER_LIMITED |
| E02 | pg | 0 | 50 | 40 | 1100.9 | 1.1722 | UNSTABLE |
| E02 | pg | 1 | 100 | 79.6 | 1095 | 1.9484 | UNSTABLE |
| E02 | pg | 2 | 200 | 162.6 | 1048.6 | 2.697 | UNSTABLE |
| E02 | pg | 3 | 400 | 329.6 | 1048.6 | 5.148 | UNSTABLE |
| E02 | pg | 4 | 800 | 681.4 | 1065.7 | 11.762 | UNSTABLE |
| E02 | pg | 5 | 1600 | 1469.2 | 1048.6 | 31.813 | UNSTABLE |
| E02 | pg | 6 | 3200 | 2602.4 | 1065.7 | 53.879 | UNSTABLE |
| E02 | pg | 7 | 6400 | 5786.4 | 1089.1 | 454.04 | UNSTABLE |
| E02 | pg | 1 | 3200 | 3341.9 | 1065.7 | -3.8053 | VALID |
| L01 | mssql | 0 | 50 | 40.8 | 1106.9 | 0.30749 | UNSTABLE |
| L01 | mssql | 1 | 100 | 80.6 | 1119 | 1.373 | UNSTABLE |
| L01 | mssql | 2 | 200 | 168.4 | 1071.5 | 0.64811 | UNSTABLE |
| L01 | mssql | 3 | 400 | 335.8 | 1071.5 | 3.4109 | UNSTABLE |
| L01 | mssql | 4 | 800 | 712.6 | 1083.2 | 20.078 | UNSTABLE |
| L01 | mssql | 5 | 1600 | 1278.2 | 1100.9 | 18.358 | UNSTABLE |
| L01 | mssql | 6 | 3200 | 2532.8 | 1220.3 | 73.946 | UNSTABLE |
| L01 | mssql | 7 | 6400 | 3969.2 | 2143.1 | 2230.7 | GENERATOR_LIMITED |
| L01 | mssql | 1 | 1600 | 1653.1 | 1065.7 | 10.73 | GENERATOR_LIMITED |
| L02 | mssql | 0 | 50 | 0 | 31.04 | 50 | VALID |
| L02 | mssql | 1 | 100 | 0 | 29.404 | 99.987 | VALID |
| L02 | mssql | 2 | 200 | 0 | 28.005 | 200.05 | VALID |
| L02 | mssql | 3 | 400 | 0 | 26.243 | 399.98 | VALID |
| L02 | mssql | 4 | 800 | 0 | 25.404 | 799.95 | VALID |
| L02 | mssql | 5 | 1600 | 0 | 27.854 | 1600.7 | VALID |
| L02 | mssql | 6 | 3200 | 0 | 52.487 | 3199.7 | VALID |
| L02 | mssql | 7 | 6400 | 0 | 81.827 | 6452.4 | VALID |
| L02 | mssql | 1 | 3200 | 0 | 24.065 | 3199.7 | VALID |
| L03 | mssql | 0 | 50 | 50 | 80.757 | 41.854 | VALID |
| L03 | mssql | 1 | 100 | 100 | 65.799 | 92.917 | VALID |
| L03 | mssql | 2 | 200 | 0 | 70.704 | 201.64 | VALID |
| L03 | mssql | 3 | 400 | 0 | 82.99 | 401.83 | VALID |
| L03 | mssql | 4 | 800 | 0 | 77.34 | 803.45 | VALID |
| L03 | mssql | 5 | 1600 | 0 | 71.757 | 1609.5 | VALID |
| L03 | mssql | 6 | 3200 | 0 | 58.49 | 3192.4 | VALID |
| L03 | mssql | 7 | 6400 | 0 | 44.858 | 6417.7 | VALID |
| L03 | mssql | 1 | 3200 | 0 | 25.542 | 3200.2 | VALID |
| L04 | mssql | 1 | unavailable | 66.667 | 837.9 | unavailable | VALID |
| C01 | mssql | 0 | 50 | 43.2 | 1187.7 | 0.063474 | VALID |
| C01 | mssql | 1 | 100 | 88 | 1187.7 | 3.3139 | UNSTABLE |
| C01 | mssql | 2 | 200 | 196.2 | 1187.7 | 11.517 | UNSTABLE |
| C01 | mssql | 3 | 400 | 375.8 | 1260.6 | 6.8074 | UNSTABLE |
| C01 | mssql | 4 | 800 | 713.8 | 1427.7 | 22.479 | UNSTABLE |
| C01 | mssql | 5 | 1600 | 903.2 | 2427.3 | 610.01 | UNSTABLE |
| C01 | mssql | 6 | 3200 | 751.4 | 8572.3 | 2375.9 | UNSTABLE |
| C01 | mssql | 1 | 800 | 826.13 | 1162.2 | 2.9544 | UNSTABLE |
| P01 | mssql | 0 | 50 | 34.4 | 4403.8 | 2.1009 | UNSTABLE |
| P01 | mssql | 1 | 100 | 33 | 3951.8 | 64.122 | UNSTABLE |
| P01 | mssql | 2 | 200 | 116.6 | 15789 | 28.17 | UNSTABLE |
| P01 | mssql | 1 | 50 | 53.667 | 4881 | -15.507 | VALID |
| R01 | mssql | 0 | 50 | 43.2 | 3269.5 | 4.6927 | UNSTABLE |
| R01 | mssql | 1 | 100 | 86.8 | 3113.9 | 6.4875 | UNSTABLE |
| R01 | mssql | 2 | 200 | 174.8 | 3097.1 | 10.753 | UNSTABLE |
| R01 | mssql | 3 | 400 | 346 | 3113.9 | 26.041 | UNSTABLE |
| R01 | mssql | 4 | 800 | 690.4 | 2998.1 | 41.698 | UNSTABLE |
| R01 | mssql | 5 | 1600 | 1391 | 3164.9 | 69.092 | UNSTABLE |
| R01 | mssql | 6 | 3200 | 2774.8 | 3234.2 | 114.38 | UNSTABLE |
| R01 | mssql | 7 | 6400 | 2877 | 5322.8 | 4676.5 | GENERATOR_LIMITED |
| R01 | mssql | 1 | 1600 | 1596.1 | 2362.5 | -0.95268 | VALID |
| O01 | mssql | 0 | 50 | 40.6 | 1112.9 | 0.53338 | UNSTABLE |
| O01 | mssql | 1 | 100 | 81.2 | 1220.3 | 0.48803 | UNSTABLE |
| O01 | mssql | 2 | 200 | 168.4 | 1131.2 | 2.1998 | UNSTABLE |
| O01 | mssql | 3 | 400 | 350 | 1077.4 | 4.2011 | UNSTABLE |
| O01 | mssql | 4 | 800 | 772.2 | 1143.5 | 43.772 | UNSTABLE |
| O01 | mssql | 5 | 1600 | 1271 | 1119 | 48.254 | UNSTABLE |
| O01 | mssql | 6 | 3200 | 3140 | 1260.6 | -153.62 | VALID |
| O01 | mssql | 7 | 6400 | 2946.6 | 2933.9 | 1914.4 | GENERATOR_LIMITED |
| O01 | mssql | 1 | 1600 | 1602.7 | 1083.2 | 6.6289 | UNSTABLE |
| O02 | mssql | 0 | 50 | 42 | 1054.3 | 0.046385 | VALID |
| O02 | mssql | 1 | 100 | 80.2 | 1089.1 | -0.99371 | VALID |
| O02 | mssql | 2 | 200 | 150.6 | 1330.7 | 37.447 | UNSTABLE |
| O02 | mssql | 3 | 400 | 132.8 | 6295.7 | 253.71 | UNSTABLE |
| O02 | mssql | 1 | 100 | 102.67 | 1048.6 | 0.19481 | UNSTABLE |
| O03 | mssql | 0 | 50 | 11.8 | 90392 | 37.875 | UNSTABLE |
| O03 | mssql | 1 | 25 | 3.8 | 70461 | 20.534 | UNSTABLE |
| O03 | mssql | 2 | 12 | 2.6 | 38610 | 9.0098 | UNSTABLE |
| O03 | mssql | 3 | 6 | 2.2 | 17870 | 3.6355 | UNSTABLE |
| O03 | mssql | 4 | 3 | 1.2 | 9288.8 | 1.5871 | UNSTABLE |
| O03 | mssql | 5 | 1 | 1 | 1088.3 | 0.00065339 | VALID |
| O03 | mssql | 6 | 2 | 1.2 | 5179 | 0.53671 | UNSTABLE |
| O03 | mssql | 1 | 1 | 1 | 1042.9 | 0.09631 | UNSTABLE |
| S02 | mssql | 0 | 50 | 48 | 1100.9 | 2.437 | UNSTABLE |
| S02 | mssql | 1 | 100 | 84.6 | 1112.9 | 4.3741 | UNSTABLE |
| S02 | mssql | 2 | 200 | 160.4 | 1089.1 | 7.0809 | UNSTABLE |
| S02 | mssql | 3 | 400 | 325.4 | 1083.2 | 9.3452 | UNSTABLE |
| S02 | mssql | 4 | 800 | 673.8 | 1095 | 21.968 | UNSTABLE |
| S02 | mssql | 5 | 1600 | 1561.8 | 1089.1 | 92.982 | UNSTABLE |
| S02 | mssql | 6 | 3200 | 3013.8 | 1100.9 | 46.76 | UNSTABLE |
| S02 | mssql | 7 | 6400 | 4089 | 1975.9 | 1501.4 | GENERATOR_LIMITED |
| S02 | mssql | 1 | 1600 | 1604.9 | 1089.1 | 4.7639 | UNSTABLE |
| S03 | mssql | 0 | 50 | 39.8 | 1112.9 | 0.90274 | UNSTABLE |
| S03 | mssql | 1 | 100 | 80 | 1125.1 | 0.50407 | UNSTABLE |
| S03 | mssql | 2 | 200 | 160 | 1060 | 3.594 | UNSTABLE |
| S03 | mssql | 3 | 400 | 338.8 | 1095 | -4.834 | VALID |
| S03 | mssql | 4 | 800 | 736 | 1065.7 | 22.911 | UNSTABLE |
| S03 | mssql | 5 | 1600 | 1508.8 | 1083.2 | -23.044 | VALID |
| S03 | mssql | 6 | 3200 | 2791.6 | 1162.2 | -157.99 | VALID |
| S03 | mssql | 7 | 6400 | 4213.2 | 2190 | 1267.6 | GENERATOR_LIMITED |
| S03 | mssql | 1 | 1600 | 1650.4 | 1095 | 3.6931 | UNSTABLE |
| I05 | mssql | 0 | 50 | 41.6 | 1143.5 | 0.93917 | UNSTABLE |
| I05 | mssql | 1 | 100 | 87.8 | 1220.3 | 2.3749 | UNSTABLE |
| I05 | mssql | 2 | 200 | 193.8 | 1174.9 | 11.439 | UNSTABLE |
| I05 | mssql | 3 | 400 | 378.4 | 1226.9 | 18.488 | UNSTABLE |
| I05 | mssql | 4 | 800 | 628.8 | 1213.7 | -6.3202 | VALID |
| I05 | mssql | 5 | 1600 | 870 | 3047.2 | 646.93 | UNSTABLE |
| I05 | mssql | 1 | 400 | 384.93 | 1137.3 | -0.60676 | VALID |
| M01 | mssql | 0 | 50 | 41 | 1207.1 | 0.035046 | VALID |
| M01 | mssql | 1 | 100 | 82.4 | 1119 | 1.2745 | UNSTABLE |
| M01 | mssql | 2 | 200 | 174.4 | 1100.9 | 3.439 | UNSTABLE |
| M01 | mssql | 3 | 400 | 369.6 | 1155.9 | 14.533 | UNSTABLE |
| M01 | mssql | 4 | 800 | 702.4 | 1174.9 | -10.897 | VALID |
| M01 | mssql | 5 | 1600 | 1398.8 | 1112.9 | 110.92 | UNSTABLE |
| M01 | mssql | 6 | 3200 | 2550.6 | 1316.4 | 274.51 | GENERATOR_LIMITED |
| M01 | mssql | 1 | 800 | 813.4 | 1112.9 | -0.87755 | VALID |
| M02 | mssql | 0 | 50 | 42 | 1220.3 | 1.2396 | UNSTABLE |
| M02 | mssql | 1 | 100 | 88.2 | 1247 | 5.9899 | UNSTABLE |
| M02 | mssql | 2 | 200 | 166.6 | 1247 | 1.7201 | UNSTABLE |
| M02 | mssql | 3 | 400 | 278.2 | 1944 | 92.304 | UNSTABLE |
| M02 | mssql | 4 | 800 | 259 | 7366.3 | 509.5 | UNSTABLE |
| M02 | mssql | 1 | 200 | 204.07 | 1137.3 | -0.62243 | VALID |
| M03 | mssql | 0 | 50 | 45.8 | 1933.5 | -2.4871 | VALID |
| M03 | mssql | 1 | 100 | 94.2 | 1811.9 | -7.314 | VALID |
| M03 | mssql | 2 | 200 | 29 | 6261.7 | 167.69 | UNSTABLE |
| M03 | mssql | 1 | 50 | 49.867 | 1253.7 | -0.049223 | VALID |
| M04 | mssql | 0 | 50 | 40.8 | 1119 | 1.385 | UNSTABLE |
| M04 | mssql | 1 | 100 | 81.8 | 1125.1 | 1.9694 | UNSTABLE |
| M04 | mssql | 2 | 200 | 184 | 1106.9 | 3.0931 | UNSTABLE |
| M04 | mssql | 3 | 400 | 369.6 | 1106.9 | 11.632 | UNSTABLE |
| M04 | mssql | 4 | 800 | 782.4 | 1089.1 | 49.191 | UNSTABLE |
| M04 | mssql | 5 | 1600 | 1432.2 | 1174.9 | -68.818 | VALID |
| M04 | mssql | 6 | 3200 | 2317 | 1643.6 | 623.44 | GENERATOR_LIMITED |
| M04 | mssql | 1 | 800 | 792 | 1131.2 | -1.4147 | VALID |
| E01 | mssql | 1 | 35 | 34.85 | 1352.5 | -0.026084 | VALID |
| E02 | mssql | 0 | 50 | 40.2 | 1106.9 | 0.85579 | UNSTABLE |
| E02 | mssql | 1 | 100 | 81 | 1112.9 | 1.5064 | UNSTABLE |
| E02 | mssql | 2 | 200 | 163.2 | 1071.5 | 2.795 | UNSTABLE |
| E02 | mssql | 3 | 400 | 337.2 | 1077.4 | 6.504 | UNSTABLE |
| E02 | mssql | 4 | 800 | 781.2 | 1077.4 | 32.118 | UNSTABLE |
| E02 | mssql | 5 | 1600 | 1312.2 | 1106.9 | 87.936 | UNSTABLE |
| E02 | mssql | 6 | 3200 | 2938 | 1181.2 | -42.94 | VALID |
| E02 | mssql | 7 | 6400 | 4048 | 2311.9 | 4535.1 | GENERATOR_LIMITED |
| E02 | mssql | 1 | 1600 | 1628.3 | 1071.5 | 1.2144 | VALID |
