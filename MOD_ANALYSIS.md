# RepairRequiresMaterials 현재 구현 분석

> 기준: 2026-08-30 작업 트리
>
> 범위: 정적 코드 구조와 로컬 Valheim 어셈블리를 기준으로 한 설계 설명. 실제 게임 및 멀티플레이 실기 검증은 별도 항목이다.

## 결론

현재 작업 트리는 제작대 전용 재료 수리와 고정 확률적 수리비 반올림, 확률적 반환량을 사용하는 인시너레이터 아이템 분해, Crafting 스킬 기반 결정론적 무료 수리 ticket, 로컬 관리자용 일괄 내구도 명령을 제공한다. 기존 바닐라 수리 버튼은 그대로 사용하고, 별도의 수리 패널이나 버튼을 만들지 않는다. 인시너레이터의 일반 Use도 건드리지 않고 설정 가능한 modifier+Use만 분해 경로로 분기한다.

- 플레이어가 현재 사용 중인 제작대의 종류와 최소 레벨을 모두 충족하는 손상 장비만 수리 목록에 포함한다.
- 조건을 충족하지 않는 장비는 경고 문구를 표시하는 대신 처음부터 목록에서 제외한다.
- 수리 비용은 선택 장비의 정확한 출력 레시피에 들어가는 재료로만 지불한다.
- 제작대 밖 수리, 바이옴별 수리 분말, 분말 자동 전환은 제거한다.
- UI는 바닐라 수리 버튼 왼쪽에 붙는 투명 아이콘 스트립만 사용한다.
- 비용이 1개 이상인 수리는 장비별 결정론적 ticket이 무료이면 재료 아이콘 대신 `FREE`를 표시하고 소비를 생략한다.
- Crafting 생산 보너스는 레벨 100에서의 직접 확률을 0~25%로 설정하고 기본 25%를 아이템마다 독립 판정한다.
- Jotunn의 프리팹, 레시피, 현지화 및 Woodpanel API가 더 이상 필요하지 않다.
- 인시너레이터에는 기존 장비 타입 allowlist 또는 `Additional Dismantleable Items`에 명시된 비장비 중 exact-prefab 레시피와 소유자 검증을 통과한 항목만 분해한다.
- `rrm_setdurability <0-100>`은 호스트 또는 관리자 자신의 인벤토리 장비 내구도만 일괄 변경하고 무료 수리 ticket은 건드리지 않는다.
- 인시너레이터 분해는 항상 요청자가 습득한 레시피의 대상 아이템만 처리하며, 요청 순간의 거리만 요구한다.

## 의존성

| 항목 | 상태 |
|---|---|
| BepInExPack Valheim | 5.4.2333 이상, 필수 |
| ServerSync | 최종 DLL에 병합 |
| AzuCraftyBoxes | 선택 지원, soft dependency |
| Jotunn | 제거 |
| ValheimEnchantmentSystem | 사용하지 않음 |

서버 동기화가 필요한 모드이므로 서버와 모든 클라이언트에 동일한 버전을 설치해야 한다.

## 수리 후보 판정

후보 목록은 플레이어 인벤토리의 손상된 장비를 대상으로 다음 조건을 순서대로 검사한다.

1. 장비가 내구도를 사용하고 수리 가능한 상태인지 확인한다.
2. 현재 제작대가 존재하고 플레이어가 사용할 수 있는 상태인지 확인한다.
3. 장비의 정확한 출력 프리팹에 대응하는 안전한 레시피를 찾는다.
4. 레시피가 요구하는 제작대 종류가 현재 제작대와 일치하는지 확인한다.
5. 현재 제작대 레벨이 레시피의 최소 레벨 이상인지 확인한다.

모든 조건을 통과한 장비만 아이콘 스트립과 휠 전환 대상이 된다. 재료가 부족한 장비는 제작대 조건 자체는 충족하므로 목록에 남지만, 수리 버튼은 결제 가능 여부에 따라 비활성화된다.

정확한 제작대 판정은 바닐라 `InventoryGui.CanRepair`를 따른다. 따라서 NG+의 `item.m_worldLevel < Game.m_worldLevel` 제작대 종류 예외와 `Mathf.Min(station.GetLevel(), 4)` 레벨 상한도 그대로 유지한다.

레시피는 표시 이름이 아니라 정확한 출력 프리팹으로 인덱싱한다. 같은 현지화 이름을 공유하는 다른 모드 장비의 레시피를 잘못 선택하는 문제를 피하기 위한 방식이다. 선택 장비 자체를 재료로 소비하는 순환 레시피는 안전하지 않은 수리 비용으로 간주해 제외한다.

## 재료 비용과 결제

재료 비용은 품질 1의 기본 제작량 `B`, 레시피의 레벨당 증가량 `P`, 현재 품질과 손실 내구도 구간을 조합해 계산한다. 기본 제작량과 품질 추가분은 서로 다른 서버 동기화 비율을 사용한다.

```text
내구도 구간 = ceil(현재 내구도 비율 × 10) × 10

완전수리 기준량 =
    B × Base Material Cost Percent / 100
    + (현재 품질 - 1) × P × Quality Increment Material Cost Percent / 100

raw = 완전수리 기준량 × (1 - 내구도 구간 / 100)

필요 재료량 = Floor(raw)
             + (고정 roll < raw - Floor(raw) 이면 1, 아니면 0)
```

기본값은 `Base Material Cost Percent = 15%`, `Quality Increment Material Cost Percent = 5%`다. 따라서 품질별 원시 기준은 `B`, `B+P`, `B+2P`, `B+3P`처럼 선형으로 증가하며, 기본량과 품질 추가량에 각각 15%와 5%를 적용한다. 이전 품질에서 지불한 업그레이드 비용을 누적 합산하지 않는다. 두 성분과 손실 내구도를 모두 곱한 뒤 마지막에 한 번만 확률적으로 정수화한다. `raw=0.1`이면 10% 확률로 1개, `raw=1.8`이면 1개 확정에 80% 확률로 1개를 더 요구한다.

확률 표본은 장비 custom data에 저장한 별도 GUID와 성공 수리 회차, 최종 재료 비용 식별자로부터 SHA-256으로 만든다. 여러 필수 재료는 각각 독립 표본을 쓰되 `require only one ingredient` 대체재 묶음은 하나의 공통 표본을 사용하므로, 대체재 수만큼 독립적인 최저 비용을 고르는 편향을 만들지 않는다. 내구도 구간과 비용 설정값은 seed에 넣지 않으므로 UI 재개방·목록 스크롤·재접속뿐 아니라 값을 변경했다가 되돌리는 방법으로도 재추첨할 수 없다. raw가 양수인 수리가 실제로 완료된 뒤에만 회차를 증가시키며, 모든 재료가 0개로 나온 성공 수리도 다음 회차로 넘어간다. 소비 실패나 취소는 같은 결과를 유지한다. 상태를 저장할 수 없으면 해당 preview의 소수 비용을 모두 올림하는 fail-closed 정책을 사용하고, 손상된 저장 상태도 올림 marker로 고정해 한 번의 성공 수리 전에는 새 난수를 노출하지 않는다. 반올림 방식은 고정이며 별도 config가 없다.

수리 버튼을 누르는 순간 선택 장비, 현재 제작대, 레시피, 제작대 레벨과 재료 보유량을 다시 계산한다. 화면에 표시한 계획과 달라졌으면 재료를 소비하지 않고 수리를 중단한다.

Valheim 인벤토리와 외부 컨테이너 API는 여러 재료를 하나의 트랜잭션으로 제거하는 기능을 제공하지 않는다. 사전 검증 뒤에도 동시 변경으로 후속 제거가 실패할 수 있으며, 일부 소비가 이미 확인된 경우에는 재료만 잃는 상황을 피하기 위해 수리를 완료하는 손실 방지 정책을 사용한다.

기본 인벤토리 재료가 우선적인 결제 대상이다. AzuCraftyBoxes가 설치되어 있으면 별도 연동 설정 없이 해당 모드가 허용한 주변 컨테이너의 재료도 보유량 계산과 소비에 자동으로 포함한다. AzuCraftyBoxes가 없거나 자체적으로 pull을 차단하거나 호환 API를 사용할 수 없으면 플레이어 인벤토리만 사용하는 경로로 안전하게 돌아간다. 단, 비동기 RPC 제거 결과를 같은 프레임에 검증할 수 없는 kg Item Drawer 래퍼는 재료 손실이나 무상 수리를 피하기 위해 집계와 소비에서 제외한다.

## Crafting 스킬 무료 수리 ticket

`Enable Free Repairs`가 켜져 있고 재료 비용이 실제로 1개 이상일 때 장비 custom data에 item GUID, 완료 회차, `Free`/`Paid` 결과와 비용 계획 SHA-256 fingerprint를 하나의 versioned 문자열로 기록한다. roll은 `item GUID + 회차`에서 결정론적으로 만들고, 최초 노출 시점의 유효 Crafting skill factor와 무료 수리 확률을 비교한다. 확률은 `min(Free Repair Chance At Level 0, Free Repair Chance At Level 100) + skill factor × (Free Repair Chance At Level 100 - 실효 Level 0 값)`으로 계산한다. 기본값은 10%와 30%이므로 Crafting 50에서 20%다. 이미 기록된 `Free`/`Paid` 결과에는 이후 설정 변경을 적용하지 않고 정상 수리로 회차가 증가한 다음 ticket부터 새 값을 사용한다.

`Free`이면 원래 재료 계획은 검증용으로 유지하되 HUD는 재료 슬롯을 숨기고 금색 `FREE` 하나만 표시한다. 재료 보유 여부와 무관하게 바닐라 버튼으로 수리하며, 인벤토리 및 AzuCraftyBoxes 소비 경로는 호출하지 않는다. `Paid`이면 기존 보유량/필요량과 소비 경로를 그대로 사용한다.

무료 결과가 표시된 뒤 품질, 내구도 10% 비용 구간, 재료 prefab 또는 필요량이 바뀌면 같은 회차를 영구 `Paid`로 잠근다. 원래 상태로 되돌려도 무료가 복구되거나 재추첨되지 않는다. 확률 정수화 뒤 실제 비용이 1개 이상인 계획만 새 무료 ticket을 만든다. raw가 양수지만 모든 재료가 0개로 나온 수리가 완료되면 수리비 반올림 회차는 항상 증가하며, 이미 존재하는 무료 ticket도 함께 다음 회차로 넘어간다. raw 자체가 0인 수리와 소비 시작 전 실패는 회차를 유지한다. item custom data는 인벤토리 저장, 드롭 ZDO와 clone에 보존되므로 UI 재개방, 장비 이동, 드롭·회수와 정상 재접속으로 결과를 바꿀 수 없다.

## Crafting 생산 보너스

Crafting 스킬이 지정된 제작대에서 stackable 결과물은 바닐라의 고정 1개 및 묶음 누적 계산 대신 기본 생산 아이템마다 독립 판정한다. 확률은 바닐라 bonus chance를 다시 곱하지 않고 설정값을 레벨 100의 직접 확률로 사용한다.

```text
아이템당 추가 생산 확률(%) = 유효 Crafting skill factor
                         × Bonus Output Chance At Level 100(%)
```

`Bonus Output Chance At Level 100`의 범위는 `0-25%`, 기본값은 `25%`다. 따라서 Crafting 레벨 0·50·100에서는 각각 0%·12.5%·25%가 된다. 기본 생산량이 20개면 20번 독립 판정하고 성공 횟수가 K일 때 정확히 K개만 추가한다. 추가 생산물은 다시 판정하지 않으며, 성공 결과는 기존 제작 완료의 `+K` 표시와 bonus effect 한 번으로 합친다. `Bonus Output Excluded Prefabs`와 일치하는 결과물, 비 stackable 결과물, Crafting 이외의 station skill과 비정상적으로 큰 제작 묶음의 기존 예외 경로는 유지한다.

## 관리자 일괄 내구도 명령

`rrm_setdurability <0-100>`은 호스트에서는 즉시 실행하고, 원격 클라이언트에서는 서버에 승인 RPC를 보낸다. 서버는 요청을 보낸 실제 `ZRpc` 연결의 관리자 권한을 확인하며, 승인 응답을 받은 클라이언트만 자신의 인벤토리를 한 번 순회한다. 기존 인시너레이터 분해와 같은 equipment type allowlist에 `m_useDurability`와 유효한 품질 반영 최대 내구도 조건을 더하므로, 장착 여부와 무관하게 무기, 방패, 도구, 방어구, 망토, 횃불, utility와 trinket만 포함하고 탄약·재료·소모품은 제외한다. 각 값은 `GetMaxDurability() × n / 100`으로 설정하고 실제 변경 뒤 `Inventory.Changed()`를 한 번만 호출한다.

Valheim의 `onlyAdmin` 표시는 로컬 명령 권한을 완전하게 검사하지 않고, `isCheat` 명령은 전용 서버의 관리자 클라이언트에서 로컬 실행될 수 없으므로 명령 자체는 로컬 일반 명령으로 유지한다. 대신 direct `ZRpc` 승인 왕복으로 실제 연결의 관리자 여부를 서버에서 판정한다. 플레이어 인벤토리는 client-owned이므로 서버는 승인만 담당하고 다른 플레이어를 대상으로 하는 명령은 제공하지 않는다.

이 명령은 재료 소비, Crafting 스킬 증가, `RepairCostRoundingSystem.CompleteSuccessfulRepair` 또는 `CraftingFreeRepairSystem.CompleteSuccessfulRepair`를 호출하지 않는다. 따라서 기존 재료 반올림 결과와 `Free`/`Paid` 판정 및 회차를 모두 유지하고, 명령을 100%로 실행해도 정상 수리 완료로 취급하지 않는다. 정상 수리로 회차가 증가한 뒤 명령으로 다시 손상시킨 경우에만 다음 제작대 preview에서 새 결과가 자연스럽게 결정된다.

## 투명 아이콘 스트립

기존 목재 패널 UI는 제거한다. 새 UI는 바닐라 수리 버튼을 기준으로 배치되는 배경 없는 가로 스트립이다.

```text
[필요 재료 ...] [선택 장비] [마우스 휠 표시] [바닐라 수리 버튼]
```

- 장비와 재료 이미지는 인벤토리에서 사용하는 `ItemData.GetIcon()` 결과를 그대로 사용한다.
- 휠 안내 이미지는 `KeyHints.m_buildHints`에 로드된 바닐라 `mousew_icon` Sprite를 찾아 그대로 공유한다.
- 각 아이콘 주변에 목재 슬롯이나 패널 배경을 만들지 않는다.
- 재료 이름은 화면에 출력하지 않고 `현재량/필요량`을 아이콘 하단 중앙에 표시한다. AzuCraftyBoxes가 없으면 현재량은 플레이어 인벤토리만 뜻하고, 설치되어 있으면 해당 모드가 허용한 주변 컨테이너까지 자동으로 합산한다.
- 재료는 수리 버튼 쪽에서 바깥쪽으로 이어지도록 오른쪽 기준으로 정렬한다.
- 장비 아이콘은 현재 휠 선택 대상을 나타낸다.
- 마우스 포인터가 바닐라 수리 버튼 위에 있을 때 휠을 굴리면 수리 가능한 장비가 순환 선택된다.
- 최종 수리 동작은 별도 버튼이 아니라 바닐라 수리 버튼의 기존 클릭 경로를 사용한다.

투명 스트립은 고정 크기 패널보다 해상도와 UI 배율에 유연하지만, 요구 재료가 매우 많은 모드 장비에서는 왼쪽으로 길어질 수 있다. 다수 재료 레시피와 다른 UI 모드가 수리 버튼 주변에 요소를 추가하는 경우는 실기 검증이 필요하다.

## 인시너레이터 건설 비용과 아이템 분해

동기화된 `Incinerator Build Recipe`는 canonical `incinerator` prefab의 `Piece.m_resources`만 교체한다. 기본값은 바닐라와 같은 `Iron:8,Copper:4,Thunderstone:1`이며, 쉼표·세미콜론·줄바꿈으로 구분한 exact `ItemPrefab:Amount` 형식을 사용한다. 모든 항목을 양의 정수와 등록된 `ItemDrop`으로 먼저 검증한 뒤 배열 전체를 한 번에 적용하며, 빈 값이나 잘못된 정의는 원본 건설 레시피를 복원한다. `None`은 명시적인 무료 건설이다. 이 설정은 일반 소각 conversion과 Alt+Use 분해 반환 계산을 변경하지 않는다.

일반 E는 기존 `Incinerator.OnIncinerate` 경로를 그대로 통과한다. 로컬 `Modifier Key` 기본값인 `LeftAlt`를 누른 채 현재 Valheim Use 입력을 사용하면 정확히 vanilla `incinerator` 레버의 `Switch.Interact` Prefix에서만 원본을 차단하고 별도 owner RPC를 전송한다. modifier 경로가 감지된 뒤 실패하더라도 일반 소각으로 폴백하지 않는다.

기본 허용 타입은 `Tool`, `OneHandedWeapon`, `TwoHandedWeapon`, `TwoHandedWeaponLeft`, `Bow`, `Shield`, `Torch`, `Helmet`, `Chest`, `Legs`, `Shoulder`, `Utility`, `Trinket`이다. 바닐라 `IsEquipable()`에 포함되는 `Ammo`는 기본 집합에서 제외하지만, 동기화된 `Additional Dismantleable Items`가 exact prefab 또는 전체 이름 `*` 패턴으로 일치시키는 비장비는 예외적으로 허용한다. quest item, blacklist prefab과 정확한 안전 레시피가 없는 아이템은 항상 그대로 남긴다. 추가된 stackable item은 해당 ItemData의 전체 스택을 한 작업에서 처리한다.

known-recipe 검사는 항상 적용되며 별도 설정이 없다. Valheim의 레시피 지식은 exact prefab이 아니라 출력 아이템의 `m_shared.m_name`으로 저장된다. 요청 클라이언트는 ZDO에서 갱신한 로컬 인시너레이터 inventory의 대상 아이템 이름만 추리고, 그중 `Player.IsRecipeKnown`을 통과한 distinct 이름을 domain-separated SHA-256 기반 128-bit token으로 바꿔 전송한다. 캐릭터가 아는 전체 레시피를 보내지 않으므로 요청에 관계없는 진행도 노출과 패킷 크기를 줄인다. 로컬 container mirror가 순간적으로 오래된 경우에는 알려진 대상이 이번 작업에서 그대로 남는 fail-safe false negative만 생기며 다음 요청에서 다시 평가할 수 있다. 패킷은 schema, player ID, 최대 4096개의 고정 폭 token으로만 구성하고 전체 길이, 개수, 중복, trailing byte를 owner에서 fail-closed 검증한다. owner는 작은 header와 요청자의 identity/proximity를 먼저 검증한 뒤에만 token set을 구성한다. 동일 shared name을 의도적으로 공유하는 모드 아이템은 바닐라 지식 상태도 공유한다는 제한이 있다.

known-recipe snapshot은 client-owned 캐릭터의 주장이라 modified client에 대한 서버 권위 증명은 아니다. owner는 이를 대상 허용 필터로만 사용하며, exact-output 레시피 선택, 모호한 복수 레시피 거부, 기본·업그레이드 재료량과 최종 반환량 계산은 모두 owner ObjectDB와 동기화 설정으로 다시 수행한다. 따라서 정상 클라이언트에서는 미습득 아이템이 그대로 남고, 패킷이 반환 재료 종류나 수량을 지정할 수는 없다.

분해 재료별 반환 기준은 수리 비용과 별도로 계산한다. exact-output 후보가 여러 개이면 기존의 동등 재료 비용 판정을 유지하며, 비활성·가변 재료·self recipe 등 기존 안전 규칙을 통과하지 못하는 레시피는 사용하지 않는다.

```text
B = GetAmount(1)
U = Σ GetAmount(level), level = 2..현재 품질
S = source stack / recipe output amount

raw = B × S × Base Material Return Percent / 100
    + U × S × Cumulative Upgrade Material Return Percent / 100

확정 반환량 = Floor(raw)
추가 1개 확률 = raw - Floor(raw)
```

기본값은 기본 제작 비용 10%, 누적 업그레이드 비용 20%다. 같은 재료의 raw 반환량은 한 번의 작업 전체에서 합친 뒤 확률적으로 한 번만 정수화한다. 정수 부분은 확정 지급하고 소수 부분은 추가 1개의 확률이므로 `raw=0.1`이면 10% 확률로 1개, `raw=1.5`이면 1개 확정에 50% 확률로 1개를 더 반환한다. owner는 작업마다 암호학적 128-bit seed를 한 번 만들고 material prefab별 SHA-256 표본을 계산해 애니메이션 전후 두 계획에 같은 결과를 사용한다. 모든 소수 roll이 실패해 반환 재료가 0개여도 대상 스택은 분해한다. 실패한 아이템을 남기면 같은 아이템으로 성공할 때까지 Alt+Use를 반복할 수 있기 때문이다. 기존 장비는 `recipe.m_amount == 1` 제약을 유지하고, 명시적으로 추가된 비장비는 양수 생산량을 스택 비율 계산에 사용한다. `m_requireOnlyOneIngredient`, 자기 자신을 재료로 쓰는 레시피와 정규화된 단가가 서로 다른 복수 레시피는 역산이 모호하므로 제외한다.

ZNetView owner는 요청을 받을 때 sender와 Player ZDO owner, 레버 상호작용 거리, ward, container privacy/ownership, busy 상태와 inventory fingerprint를 확인한다. 이 검증 뒤 owner가 operation seed를 한 번 생성하며 seed는 클라이언트 패킷에서 받지 않는다. 애니메이션 완료 직전에는 sender·Player identity, 생존, ward와 container 권한, ownership와 fingerprint를 다시 검사하지만 거리는 재검사하지 않는다. 정상적으로 레버를 당긴 뒤 이동한 것만으로 작업이 취소되지 않게 하기 위한 구분이다. 같은 seed로 반환 계획을 다시 계산하고, 반환 재료는 max stack으로 분할해 scratch inventory에서 전부 들어가는지 먼저 검증한다. 실제 remove/add 중 예외가 나면 원본 snapshot으로 되돌리고, 성공할 때만 container 저장을 한 번 발생시킨다.

## 검증 항목

- 제작대 종류가 다른 장비가 목록에서 제외되는지
- 제작대 최소 레벨이 부족한 장비가 목록에서 제외되는지
- 재료 부족 장비는 표시되지만 수리 버튼이 비활성화되는지
- 휠 위/아래 입력이 수리 버튼 위에서만 대상을 한 단계씩 전환하는지
- 선택 장비와 필요 수량이 휠 전환 직후 갱신되는지
- 수리 직전 재검증으로 장비나 재료 변경 경쟁 상태를 막는지
- 같은 장비·회차에서 UI 재개방, 휠 전환, 이동과 재접속 후에도 무료/유료 결과가 유지되는지
- 무료 HUD가 원래 재료 아이콘 대신 `FREE`만 표시하고 재료 없이도 수리되는지
- 표시된 무료 수리의 품질·내구도 비용 구간·재료 계획이 바뀌면 재추첨 없이 유료로 잠기는지
- `Free Repair Chance At Level 0/100` 기본값 `10/30`에서 Crafting 0·50·100이 `10/20/30%`, 설정 `5/20`에서 `5/12.5/20%`가 되는지
- `Free Repair Chance At Level 0`이 `Free Repair Chance At Level 100`보다 높으면 실효 최소값이 최대값으로 제한되고 기존 `Free`/`Paid` ticket은 유지되는지
- 비용 0 수리와 소비 시작 전 실패가 ticket 회차를 증가시키지 않는지
- 여러 해상도와 UI 배율에서 스트립이 화면 및 다른 모드 UI와 충돌하지 않는지
- AzuCraftyBoxes 미설치, 설치 후 pull 허용, 설치 후 자체 차단 각각에서 자동 연동과 기본 인벤토리 fallback이 유지되는지
- 일반 E가 기존 소각 경로를 유지하고 modifier+Use만 분해로 분기되는지
- 요청 순간 멀리 있는 RPC는 거부하지만 정상 요청 뒤 애니메이션 중 이동은 분해를 취소하지 않는지
- 설정 없이도 요청자가 모르는 대상만 남고 아는 대상만 항상 분해되는지
- `Bonus Output Chance At Level 100=25%`에서 Crafting 0·50·100의 아이템당 확률이 0%·12.5%·25%이고, 0% 설정에서는 비활성화되는지
- known-recipe 패킷이 비정상 길이, 개수, 중복 또는 trailing data를 가지면 item 변경 없이 거부되는지
- 빈 추가 목록에서는 기존 equipment 타입만 분해되고, exact·wildcard로 추가된 비장비만 예외적으로 분해되는지
- 묶음 레시피의 추가 아이템이 `stack / recipe.m_amount` 단가로 계산되고 성공 메시지가 실제 소비 수량을 표시하는지
- 품질 1~4에서 기본 제작비와 누적 업그레이드비가 서로 다른 설정 비율로 계산되는지
- 공간 부족, 내용물 변경, 권한 부족과 동시 요청 시 대상 아이템이 소실되지 않는지
- `raw=0.1`, `0.5`, `1.0`, `1.5`에서 각각 10%, 50%, 1개 확정, 1개+50% 추가 규칙으로 동작하는지
- 모든 소수 roll이 실패해 output이 비어도 대상 스택이 소비되고, 트랜잭션 실패일 때만 원상 복구되는지
- 애니메이션 전후 두 계획이 같은 operation seed와 동일한 material roll을 사용하는지
- 전용 서버와 클라이언트가 Jotunn 없이 동일한 1.0.0 DLL로 접속되는지
- `rrm_setdurability`가 비관리자를 거부하고 장착·미장착 장비를 품질 반영 최대 내구도의 입력 비율로 바꾸는지
- 일괄 내구도 명령 전후에 기존 무료 수리 ticket 결과와 회차가 그대로 유지되는지
