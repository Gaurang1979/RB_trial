# Upgrade the mobile app: Dart SDK minimum, http 1.6; flutter_secure_storage 11 as its own step, tested on Android and iOS

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #66
- Section 5 item: Upgrade the mobile app: Dart SDK minimum
- Components/files: mobile/plant_app (pubspec.yaml, pubspec.lock, lib/main.dart, lib/modules/vision/vision_api.dart), mobile/plant_core (pubspec.yaml, lib/src/charts/trend.dart)
- Depends on: #63 (merge #63 first: it adds this item)

## Section 5
- [x] (done 2026-10-29, PR #66: Dart SDK minimum 3.9, http 1.6, flutter_secure_storage 11.2 on its own AES-GCM key store, every direct package current on Flutter 3.47.8; the app was not yet released, so no stored data needed migrating) Upgrade the mobile app: Dart SDK minimum, http 1.6; flutter_secure_storage 11 as its own step, tested on Android and iOS.

## Timeline (section 3)
| 2026-10-29 | Mobile app on Flutter 3.47.8 with every direct package current (http 1.6, flutter_secure_storage 11.2) | REQUIREMENTS 13 DEP-03 |

## Decisions (section 4)
| Secure storage majors once the app is in use | flutter_secure_storage drops data saved by older formats when a major is skipped; this upgrade went from 9 to 11 directly only because no copy of the app was in use. Once released, each major of it is taken one at a time, and the app checks checkUpgradeStatus() so a lost sign-in asks the user to sign in again | REQUIREMENTS 13 DEP-03, DEP-04 |

## Pending
- Build and sign in on a real Android phone (minSdk 24) and an iPhone with flutter_secure_storage 11 before the first release.
