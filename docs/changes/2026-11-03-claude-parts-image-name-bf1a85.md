# Studio Parts list: show each part's image name

- Status: done
- Session: parts-image-name, 2026-11-03
- Pull request: #315
- Section 5 item: Studio Parts list: show each part's image name
- Components/files: dotnet/Studio (PartsViewModel, PartsView.axaml, English string table), Studio tests; requirements section 12 (one image name)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #315: own column after the part id, from PartResult.ImageId, blank for a gap or a part with no image kept; English tooltip "Image name") Studio Parts list: show each part's image name (it is in PartResult.ImageId already; needs a column string in the 15 languages).

## Timeline (section 3)
| 2026-11-03 | Vision Studio Parts list shows each part's image name (`PartRow.Image` from `PartResult.ImageId`); view-model test | 12 (one name for each image), 6.1 |

## Update log (section 8)
| 2026-11-03 | Requirements section 12 "One name for each image": the Studio Parts list shows the image name | Claude (Mac, branch claude/parts-image-name-bf1a85) |

## Pending
- Studio Parts list "Image name" tooltip (`parts.image`) in the other 14 languages, once Vision Studio ships string tables beyond `dotnet/Studio/i18n/en.json` (only English exists today).
