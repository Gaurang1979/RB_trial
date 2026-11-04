# Runtime contract: reads without a token on the local endpoint (nobody signed in), changes need sign-in

- Status: done
- Session: Claude desktop (hybrid-app), 2026-10-30
- Pull request: #<n>
- Section 5 item: Hybrid 0: runtime gRPC contract
- Components/files: proto/visionstudio/remote/v1/runtime.proto (comments only), dotnet/RuntimeContract/Stub/StubServices.cs, dotnet/RuntimeContract.Tests/StubServerTests.cs
- Depends on: none (#85 merged)

## Section 5
- Follow-up 2026-10-30: a call with no token on the local endpoint reads (status, frames, counters, jobs) as the line PC's screen with nobody signed in; changing calls then fail with UNAUTHENTICATED ("Sign in first"); an unknown or expired token fails every call with UNAUTHENTICATED. Asked for by Hybrid 2 (#86).

## Decisions (section 4)
| Nobody signed in | A call with no token on the local endpoint is the line PC's own screen with nobody signed in (UI-UX-SPEC principles 10 and 13): it may read but not change; an unknown or expired token (after a runtime restart) is UNAUTHENTICATED on every call so the client drops it and reads without one. Over the network a token is always needed | ARCHITECTURE 2.2, UI-UX-SPEC 1 |

## Pending
- The real runtime (Hybrid 1) must answer calls without a token (ListUsers and the reads) only on the local endpoint, never over the network.
