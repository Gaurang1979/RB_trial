# Consolidated ranked methods backlog for every tool (0-12), with a test per method and the ViDi-style AI plan

- Status: done
- Session: friendly-clarke-8d3e0b, 2026-11-01
- Pull request: #223
- Section 5 item: new
- Components/files: docs/vision-system/TOOL-METHODS-BACKLOG.md (new); links added from docs/vision-system/INDUSTRY-METHODS.md and docs/vision-system/ROADMAP.md
- Depends on: none (docs only; builds on #149 INDUSTRY-METHODS, #145 ROADMAP, AI-DEEP-LEARNING.md, RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md)

## Section 5
- New: [x] (done 2026-11-01, PR #223: docs/vision-system/TOOL-METHODS-BACKLOG.md) One consolidated, ranked backlog of the world's best methods (vendor + patented) to develop and test for every tool 0-12 to reach near-100%, including the ViDi-style AI plan. Per-tool tables carry a test per method, current build state with PR numbers, gain/effort and a line-safety note; one cross-tool ranked list (27 items) that development picks from one at a time, each row a future section-5 item.

## Timeline (section 3)
| 2026-11-01 | New docs/vision-system/TOOL-METHODS-BACKLOG.md: consolidated ranked methods backlog for all 13 tools (0-12). Extends INDUSTRY-METHODS (#149) by adding Tool 11 (Multi-Camera & Enterprise) and Tool 12 (Scripting), a test per method, build state with PR numbers (Have/Partial/Missing), a line-safety note per OK/NG method, a concrete three-tool ViDi plan (Blue/Red/Green) plus beyond-ViDi methods (foundation pretraining, PatchCore/PaDiM, few-shot, synthetic data, active learning, TTA, uncertainty/abstention), and one cross-tool gain-per-effort ranked list of 27 items. Marked done since the last survey: DM damaged/missing L (#171, 62->72), DM PowerGrid (#218, 72->75), multi-frame SR (#175), PSF deblur (#164), locator recall (#159), OCR two-reader (#162), layout prior (#160). | requirements 15, 16 |

## Decisions (section 4)
| Methods backlog is the pick-from list | TOOL-METHODS-BACKLOG.md §15 is the single ranked backlog development picks from one at a time; each row becomes a section-5 item when claimed. INDUSTRY-METHODS §17 stays as the measured reader-only detail it supersedes. Patents are cited as the idea's source per the owner's 2026-11-01 rule, never as a hold. | requirements 15, 16 |

## Update log (section 8)
| 2026-11-01 | New docs/vision-system/TOOL-METHODS-BACKLOG.md; link added from INDUSTRY-METHODS.md §17 and ROADMAP.md intro | Claude (Mac, branch claude/friendly-clarke-8d3e0b) |

## Pending
- Build items one at a time from TOOL-METHODS-BACKLOG.md §15, top down (each becomes its own section-5 item when claimed); items 1, 4, 5, 6, 9 are blocked on owner line time / images.
