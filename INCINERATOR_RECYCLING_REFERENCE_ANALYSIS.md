# Incinerator 장비 분해 모드 비교 및 RepairRequiresMaterials 설계 검토

작성일: 2026-08-04

분석 대상: ZenRecycle 1.2.4, IncineratorControl 1.0.2
분석 방식: DLL을 실행하지 않은 ILSpy 정적 디컴파일

## 결론

RepairRequiresMaterials에는 **Jötunn 없이** 구현하는 편이 맞다. 현재 프로젝트에 이미 exact-prefab 레시피 인덱스, ServerSync, 필수 버전 핸드셰이크가 있으므로 새 의존성을 추가할 이유가 없다.

권장 구조는 다음과 같다.

```text
일반 Use(E)
  → 아무것도 가로채지 않음
  → vanilla Incinerator.OnIncinerate 그대로 실행

Alternate Use(기본 Alt+E)
  → 정확히 incinerator 레버의 Switch.Interact에서만 가로챔
  → 별도 namespaced RPC 요청
  → ZNetView owner가 권한·레시피·공간을 재검증
  → 분해 가능한 장비만 제거하고 재료를 같은 컨테이너에 추가
```

ZenRecycle의 품질 누적 계산은 참고할 가치가 있지만 `Incinerate` transpiler, 전체 `RemoveAll()`, 무작위 재료 하나 반환은 가져오면 안 된다. IncineratorControl의 별도 컴포넌트와 owner RPC 구조는 좋은 출발점이지만, 전역 `OnIncinerate` 대체와 레시피/권한/슬롯 처리는 새로 작성해야 한다.

### 0.3.0 구현 반영

이 권장안은 0.3.0 작업 트리에 `IncineratorDismantleController`, `IncineratorDismantleCostSystem`, `IncineratorDismantlePatches`로 구현됐다. 실제 기본값은 품질 1 제작비 50%, 누적 업그레이드비 25%이며, 입력은 로컬 `KeyCode` modifier 기본 `LeftAlt`와 현재 Valheim Use 입력을 결합한다. 초기 안전 범위는 `recipe.m_amount == 1`인 exact/unambiguous recipe로 제한하고 확인 팝업 없이 즉시 owner 검증과 rollback-protected transaction을 수행한다.

### 0.6.0 확률적 정수화

0.6.0에서는 작업 전체의 같은 재료 raw 값을 합산한 뒤 정수 부분은 확정 반환하고 소수 부분은 추가 1개의 확률로 사용한다. 예를 들어 `raw=0.1`은 10% 확률로 1개, `raw=1.5`는 1개 확정과 50% 확률의 추가 1개가 된다. ZNetView owner가 작업별 seed를 한 번 생성하고 애니메이션 전후 계획에서 같은 material별 결과를 재현한다. 모든 확률 반환이 실패해도 장비는 소비하므로 동일 장비를 성공할 때까지 반복 투입하는 재추첨은 불가능하다.

## 디컴파일 산출물

원본과 해시, 도구 정보는 [refer/README.md](./refer/README.md)에 기록했다.

- ZenRecycle 핵심: [IncineratorPatch.cs](./refer/ZenRecycle/ZenRecycle/IncineratorPatch.cs), [Recycle.cs](./refer/ZenRecycle/ZenRecycle/Recycle.cs), [Configs.cs](./refer/ZenRecycle/ZenRecycle/Configs.cs)
- IncineratorControl 핵심: [IncineratorManager.cs](./refer/IncineratorControl/IncineratorControl/Managers/IncineratorManager.cs), [Recycle.cs](./refer/IncineratorControl/IncineratorControl/Managers/Recycle.cs), [IncineratorControlPlugin.cs](./refer/IncineratorControl/IncineratorControl/IncineratorControlPlugin.cs)

IncineratorControl 아래의 `ServerSync/`와 `YamlDotNet/`은 모드가 ILRepack으로 병합한 제3자 코드다. 비교할 때 모드 고유 구현과 구분해야 한다.

## Vanilla 입력 경로가 중요한 이유

현재 Valheim의 입력 흐름은 다음과 같다.

```text
Player.Update
  → Use와 AltPlace/JoyAltPlace 상태 계산
  → Player.Interact(target, hold, alt)
  → Switch.Interact(character, hold, alt)
  → m_onUse(this, character, null)
  → Incinerator.OnIncinerate(...)
```

`Switch.Interact`는 `alt`를 인자로 받지만 callback에는 전달하지 않는다. 따라서 `Incinerator.OnIncinerate`에 도달했을 때는 일반 E인지 Alt+E인지 구분할 정보가 이미 사라져 있다.

이 때문에 입력 분기점은 `Incinerator.OnIncinerate`가 아니라 `Switch.Interact` Prefix여야 한다. 단, 모든 Switch를 건드리지 않도록 다음을 모두 확인해야 한다.

```text
parent Incinerator가 존재
&& incinerator.m_incinerateSwitch == __instance
&& root prefab 이름이 incinerator
```

일반 입력이면 Prefix는 상태를 전혀 바꾸지 않고 원본을 통과시킨다. 분해 modifier가 감지된 순간에는 성공 여부와 관계없이 원본을 막아야 한다. 실패 후 vanilla로 폴백하면 장비가 분해되지 않은 채 소각될 수 있다.

## ZenRecycle 분석

### 처리 경로

ZenRecycle은 일반 E를 별도 분해 동작으로 나누지 않는다.

1. `Incinerator.OnIncinerate` Prefix에서 사용당 요구 아이템을 확인한다.
2. vanilla owner RPC와 `Incinerate` coroutine을 그대로 탄다.
3. coroutine 안의 `Inventory.RemoveAll()`을 transpiler로 교체한다.
4. vanilla conversion이 소비하고 남긴 모든 아이템을 `Recycle.Process()`에 넣는다.
5. 컨테이너를 전부 비운 뒤 재활용 결과와 vanilla 결과를 다시 넣는다.

장점은 vanilla의 owner-authoritative 실행과 애니메이션을 그대로 재사용한다는 점이다. 반면 특정 coroutine IL과 지역 변수 배치에 의존하고 일반 E 자체를 바꾸므로 이번 요구에는 맞지 않는다.

### 레시피와 반환 계산

출력 prefab 이름별 recipe cache를 만들고 `recipe.m_amount`가 작은 첫 recipe를 선택한다. shared localization name 충돌은 피하지만 다음 문제가 있다.

- `recipe.m_enabled`를 무시한다.
- 복수 recipe가 실제로 어떤 재료로 제작됐는지 판별하지 않는다.
- ObjectDB가 갱신된 뒤 cache를 무효화하지 않는다.
- 각 입력 아이템에서 recipe 재료 중 **무작위 하나만** 반환한다.

품질별 기준량은 실제 업그레이드 단계 전체를 누적한다.

```text
invested(q) = Σ GetAmount(level), level = 1..q

vanilla Requirement라면
invested(q) = B + P × q × (q - 1) / 2
```

`B=40`, `P=20`인 경우 품질 1~4의 기준량은 `40, 60, 100, 160`이다. 여기에 `RecyclePercent`, `item.stack / recipe.m_amount`를 적용한다.

다만 먼저 반올림한 뒤 결과를 자원 최대 스택 하나로 잘라서, 반환량이 큰 경우 조용히 손실될 수 있다. 스택 불가 재료는 반환율과 무관하게 1개가 된다.

### 안전 및 호환 문제

- consumable 외에는 recipe가 없어도 최종 `RemoveAll()` 때문에 사라질 수 있다.
- blacklist는 환급만 막고 원본 파괴는 막지 않는다.
- 요구 아이템 검증이 요청 클라이언트에서만 수행되어 owner RPC에서 재검증되지 않는다.
- 단일 비텔레포트 재료 recipe를 막는 포털 우회 방지는 좋은 아이디어다.
- Jötunn과 Zen.ModLib가 hard dependency다.
- 다른 `Incinerate` transpiler 또는 게임 업데이트와 충돌하기 쉽다.

## IncineratorControl 분석

### 처리 경로

`ZNetScene.Awake` Postfix에서 `incinerator` prefab에 `Recycle` 컴포넌트를 추가한다. `Incinerator.OnIncinerate` Prefix는 서버 동기화 설정에 따라 다음 중 하나를 수행한다.

- `Recycle.Enabled = Off`: YAML로 구성한 vanilla conversion 표를 주입하고 원본 실행
- `Recycle.Enabled = On`: 원본을 완전히 막고 `Recycle.OnRecycle()` 실행

즉 E와 Alt+E를 나누는 구현이 아니라, 일반 E의 의미를 전역 설정으로 전환한다.

### 별도 RPC와 계산

`Recycle` 컴포넌트가 `RPC_RequestRecycle`을 등록하고 ZNetView owner에서 coroutine을 실행하는 구조는 참고할 만하다. vanilla 레버 애니메이션과 응답 RPC도 재사용한다.

그러나 실제 계산은 다음과 같다.

- `ObjectDB.instance.GetRecipe(item)` 사용: 같은 shared name을 가진 modded item과 충돌 가능
- 각 재료의 기본 `m_amount`만 사용
- 품질 증가분, `item.stack`, `recipe.m_amount`, 내구도 무시
- 최종 `CeilToInt(total × Rate)`
- `m_requireOnlyOneIngredient`는 owner의 `Player.m_localPlayer`가 아는 첫 재료 선택

`CeilToInt` 때문에 반환율이 0보다 크기만 하면 재료 종류마다 최소 1개가 생길 수 있다. dedicated server의 owner 처리에서 `Player.m_localPlayer`를 쓰는 것도 안전하지 않다.

### 안전 및 호환 문제

- 요청자 측에서 vanilla의 `HasOwner()` 대신 `IsOwner()`를 요구해 현재 소유권에 따라 요청이 거절될 수 있다.
- owner RPC가 sender UID, 전달된 player ID, 거리, ward 권한을 다시 검증하지 않는다.
- 반환 공간과 `Inventory.AddItem` 성공 여부를 확인하지 않는다.
- 장비를 먼저 제거한 뒤 add 실패를 무시하므로 재료가 손실될 수 있다.
- `StartRecycle()`가 `StopAOE`를 `Recycle` 컴포넌트 자신에게 `Invoke`하지만 메서드는 `Incinerator`에 있다.
- Jötunn 의존성은 없지만 ServerSync와 YamlDotNet 전체를 DLL에 병합했다.

## 핵심 비교

| 항목 | ZenRecycle 1.2.4 | IncineratorControl 1.0.2 | RepairRequiresMaterials 권장 |
|---|---|---|---|
| 실행 입력 | 일반 E | 일반 E, 전역 모드 전환 | 일반 E 보존, alternate Use만 분기 |
| 주 훅 | `Incinerate` enumerator transpiler | `OnIncinerate` Prefix | 정확한 레버의 `Switch.Interact` Prefix |
| 네트워크 | vanilla owner RPC/coroutine 재사용 | 별도 owner RPC/coroutine | 별도 namespaced owner RPC |
| recipe 탐색 | exact prefab cache, 첫 recipe | shared-name `GetRecipe` | 기존 `RepairRecipeCatalog` exact prefab |
| 품질 | `B+P+2P+...` 누적 | 기본 B만 | 아래의 명시적 분해 공식 |
| 반환 재료 | 무작위 한 종류 | 조건에 맞는 모든 종류 | 모든 확정 재료, ambiguous recipe는 제외 |
| 반올림 | 중간 `RoundToInt` | 재료별 `CeilToInt` | 전체 집계 후 한 번 확률적 반올림 |
| 미처리 아이템 | 전체 비우기 때문에 파괴 가능 | 대체로 남김 | 반드시 그대로 남김 |
| 공간 부족 | 한 스택 cap/add 결과 미검사 | add 결과 미검사 | 사전 시뮬레이션 실패 시 전체 취소 |
| Jötunn | hard dependency | 없음 | 없음 |

## RepairRequiresMaterials 구현 권장안

### 1. 입력 설정

가장 clean한 기본값은 `Switch.Interact`가 이미 받은 `alt`를 사용하는 것이다. 그러면 Valheim의 `AltPlace + Use` 재바인딩과 게임패드를 자동으로 따른다.

물리적으로 `LeftAlt + 현재 Use 키`를 별도 설정으로 제공하려면 로컬 전용 `ConfigEntry<KeyCode>`를 쓰고 Prefix 호출 시 해당 키가 눌렸는지 검사하는 편이 낫다. BepInEx `KeyboardShortcut`은 등록되지 않은 다른 키가 동시에 눌리면 실패하도록 구현되어 있어, `KeyboardShortcut(LeftAlt).IsPressed()`는 E가 함께 눌린 상황에 적합하지 않다. 반대로 `KeyboardShortcut(E, LeftAlt)`는 Valheim의 Use 재바인딩과 어긋난다.

권장 선택지는 다음 둘 중 하나다.

- 간단하고 게임 친화적: Valheim의 `alt` 인자를 그대로 사용
- 독립 modifier가 꼭 필요함: `Dismantle Modifier Key = LeftAlt`를 `KeyCode`로 로컬 저장하고 현재 Use 입력과 결합

입력 설정과 확인창 설정은 서버 동기화하지 않는다. 기능 활성화, 환급률, blacklist 같은 게임 규칙만 서버 동기화한다.

### 2. Prefix의 실패 규칙

alternate Use가 감지되면 `hold=true` 반복 호출까지 모두 삼킨다. 다음 경우에도 일반 E로 넘기지 않고 메시지만 표시한다.

- 컨테이너가 비어 있음
- 분해 가능한 장비가 없음
- 권한 없음
- 공간 부족
- 네트워크 요청 실패

이 규칙이 없으면 사용자는 분해하려던 장비를 vanilla coal 변환이나 다른 incinerator 모드의 처리로 잃을 수 있다.

### 3. RPC 등록과 권한

`Incinerator.Awake` Postfix에서 보조 컴포넌트를 하나만 추가하고 다음처럼 GUID가 포함된 RPC 이름을 등록한다.

```text
sighsorry.RepairRequiresMaterials.RequestDismantlePreview
sighsorry.RepairRequiresMaterials.ConfirmDismantle
sighsorry.RepairRequiresMaterials.DismantleResponse
```

클라이언트는 계산된 재료 목록을 보내지 않는다. owner가 현재 컨테이너와 서버 설정으로 계획을 계산한다.

owner가 검증할 최소 조건은 다음과 같다.

1. 자신이 해당 ZNetView owner이고 기능이 서버 설정상 활성화되어 있음
2. sender UID가 주장한 player의 ZDO owner와 일치함
3. player가 살아 있고 incinerator 상호작용 거리 안에 있음
4. `Container.CheckAccess(playerID)`에 해당하는 privacy 검사 통과
5. player ID 기준 ward creator/permitted 검사 통과
6. `container.IsInUse() == false`이고 incinerator가 다른 작업 중이 아님
7. preview 이후 inventory fingerprint와 설정 revision이 변하지 않음

`PrivateArea.CheckAccess()`는 내부에서 `Player.m_localPlayer`를 사용하므로 dedicated server의 원격 player 검증에 그대로 쓰면 안 된다. sender의 `ZNetPeer.m_characterID`, Player ZDO owner, player ID와 위치를 연결하고, ward는 creator/permitted ID를 직접 비교하는 helper가 필요하다. `Container.CheckAccess`는 private이므로 `AccessTools` delegate 또는 동일한 privacy 규칙 helper로 호출한다.

### 4. 파괴 전 확인

장비 분해는 되돌릴 수 없으므로 기본 확인창을 권장한다.

```text
Iron Sword Q3 외 2개를 분해합니다.
Iron 18, Wood 4를 돌려받습니다.
```

권장 흐름은 `preview 요청 → owner 계산/token 발급 → Yes/No → token 확인 및 owner 재계산 → 실행`이다. token은 짧은 만료시간, inventory fingerprint, 설정 revision에 묶는다. 확인창을 끄더라도 실행 직전 owner 재계산은 유지한다.

### 5. 분해 대상

초기 안전 범위는 다음이 적절하다.

```text
item.IsEquipable()
&& item.m_shared.m_useDurability
&& item.m_shared.m_maxStackSize == 1
&& !item.m_shared.m_questItem
&& exact safe recipe가 하나로 확정됨
```

Ammo는 제외한다. custom data가 있는 enchanted/modded 장비는 정보가 영구 삭제되므로 기본 제외하거나 확인창에 별도 경고해야 한다.

분해 불가 장비, 일반 재료, consumable, blacklist 항목은 컨테이너에 그대로 둔다. `Inventory.RemoveAll()`은 사용하지 않는다. crafting station과 station level은 제작 조건일 뿐이므로 incinerator 분해 자격에는 적용하지 않는다.

### 6. recipe 선택 규칙

현재 프로젝트의 [RepairRecipeCatalog.cs](./RepairRecipeCatalog.cs)는 output prefab을 정확히 비교하고 ObjectDB 변경 때 cache를 무효화한다. 두 참고 모드보다 안전하므로 그대로 재사용한다.

추천 순서는 다음과 같다.

1. `m_dropPrefab`이 정확히 같은 recipe만 사용
2. enabled recipe 우선
3. `recipe.m_amount > 0` 확인
4. 자기 자신을 재료로 요구하는 recipe 제외
5. 서로 다른 재료 구성을 가진 exact recipe가 여러 개면 자동 선택하지 않고 제외
6. `m_requireOnlyOneIngredient`는 실제 제작 재료 기록이 없으므로 기본 제외
7. teleportable 장비에서 non-teleportable 재료가 나오면 포털 우회가 되므로 기본 제외

서로 다른 재료 구성을 가진 exact recipe가 여러 개면 해당 아이템은 제외한다. owner의 known material, 무작위, 현재 인벤토리 보유량으로 ambiguous recipe를 고르면 재료 변환 악용이 가능하기 때문이다.

### 7. 품질과 반환 공식

수리 비용의 `15% / 5%`는 유지비 규칙이고 분해 반환율과 의미가 다르다. 분해에는 별도 서버 설정을 둬야 한다.

실제 투입 재료를 되돌려준다는 의미라면 다음 **누적 투자량 방식**을 권장한다.

```text
B = max(GetAmount(1), 0)
U = Σ max(GetAmount(level), 0), level = 2..quality

raw = (B × BaseReturnPercent + U × UpgradeReturnPercent)
      × item.stack / recipe.m_amount
```

Vanilla에서 `U = P × q × (q - 1) / 2`다. `B=40`, `P=20`, 품질 4이고 기본/업그레이드 반환율이 각각 50%/25%라면:

```text
40 × 0.50 + (20 + 40 + 60) × 0.25 = 50
```

현재 수리 계산과 완전히 같은 품질 기준을 원하면 `U` 대신 `GetAmount(quality)`, 즉 `(q-1)P`만 사용하면 된다. 같은 장비에서 50%/50%라면 누적 방식은 80개, repair-aligned 방식은 50개를 반환한다. 어느 쪽을 쓰든 수리용 15%/5% 설정을 그대로 재사용하지 않는 것이 중요하다.

최종 정수화는 아이템마다 하지 않고 **같은 재료의 raw 값을 작업 전체에서 합산한 뒤 한 번 확률적으로 반올림**한다. `Floor(raw)`는 확정 반환하고 `raw - Floor(raw)`를 추가 1개의 확률로 사용한다. owner가 생성한 동일 operation seed와 material prefab으로 결과를 고정해 사전 공간 검사와 실제 적용이 서로 다른 roll을 사용하지 않게 한다. 확률 실패에도 장비를 소비해야 반복 요청 재추첨을 막을 수 있다. `recipe.m_amount`로 나누지 않으면 한 번에 여러 개 제작되는 recipe에서 복제 버그가 생긴다. 내구도는 두 참고 모드처럼 기본적으로 환급량에 곱하지 않는 편이 단순하다.

### 8. 원자적 컨테이너 변경

owner는 현재 inventory snapshot으로 다음을 먼저 시뮬레이션한다.

1. 분해 대상만 제거
2. 같은 prefab 재료를 합산
3. `m_maxStackSize` 단위로 모든 결과 추가
4. 모든 결과가 들어가는지 확인

시뮬레이션이 실패하면 live inventory는 전혀 바꾸지 않는다. 성공한 경우에만 같은 remove/add 계획을 적용하고 각 반환값을 확인한다. 중간 실패에 대비해 snapshot 복원 경로도 둔다. 일반 `Inventory.RemoveItem`/`AddItem` API를 사용해 Changed callback과 ZDO 저장을 발생시킨다.

공간 부족 시 재료를 바닥에 자동 드롭하는 것보다 전체 취소가 기본값으로 안전하다. 사용자가 컨테이너를 정리한 뒤 다시 시도할 수 있기 때문이다.

### 9. vanilla busy 상태와 애니메이션

owner가 작업을 수락한 즉시 incinerator의 기존 `isInUse` 상태를 잡아 일반 소각과 상호 배제한다. 현재 게임 빌드에서 이 필드는 private이므로 `AccessTools.FieldRef` 같은 명시적 접근자를 한 곳에 캡슐화하고 게임 업데이트 때 검증한다.

vanilla의 `RPC_AnimateLever`, `RPC_AnimateLeverReturn`, 효과 필드는 재사용할 수 있다. 다만 vanilla `Incinerate` coroutine 전체를 복사하거나 transpile하지 말고, 분해 컨트롤러가 짧은 별도 coroutine에서 `try/finally` 형태로 busy 상태를 반드시 해제해야 한다. 소유권 이전이나 컨테이너 열림이 발생하면 변경 전에 취소한다.

## 최소 설정안

현재 서버 동기화 설정:

```text
Enable Incinerator Dismantling = On
Incinerator Build Recipe = Iron:8,Copper:4,Thunderstone:1
Dismantle Base Return Percent = 10
Dismantle Upgrade Return Percent = 20
Dismantle Blacklist =
```

로컬 전용 0.3.0 구현값:

```text
Dismantle Modifier Key = LeftAlt
```

네트워크 RPC가 추가되므로 구현 시 minor version을 올리고 현재 `ModRequired = true` 핸드셰이크를 유지하는 것이 적절하다.

## 다른 incinerator 모드와의 호환

이 설계는 alternate Use에서 `OnIncinerate` 자체를 호출하지 않으므로 두 참고 모드의 핵심 패치와 직접 겹치지 않는다. 일반 E는 Harmony 원본 체인을 그대로 통과시킨다.

다만 여기서 “그대로”는 **설치된 모드 환경의 일반 E**를 뜻한다.

- ZenRecycle이 설치되어 있으면 일반 E는 여전히 ZenRecycle의 소각+재활용이다.
- IncineratorControl의 Recycle이 On이면 일반 E는 여전히 그 모드의 재활용이다.
- IncineratorControl의 Recycle이 Off여도 YAML conversion 표가 적용되므로 순정 표와 다를 수 있다.

RepairRequiresMaterials가 이 동작까지 강제로 vanilla로 되돌리면 다른 모드의 의도를 덮어쓰고 충돌이 커진다. strict vanilla E가 필요하면 두 플러그인을 감지해 경고하고 해당 recycle 설정을 끄도록 안내하는 편이 낫다. RPC 이름은 반드시 ModGuid로 namespace하고, `Switch.Interact` Prefix는 modifier 경로 외에는 side effect가 없어야 한다.

## 구현 전 테스트 체크리스트

- 일반 E가 RRM 단독 환경에서 vanilla와 완전히 동일함
- alternate Use 실패가 절대로 일반 소각으로 폴백하지 않음
- 빈 컨테이너, eligible 장비 없음, 공간 부족 시 inventory가 변하지 않음
- 품질 1~4, `recipe.m_amount > 1`, 같은 재료가 여러 장비에 중복되는 경우 계산 검증
- multiple recipe, `m_requireOnlyOneIngredient`, self recipe, quest/custom-data 장비 제외 검증
- 반환량이 max stack을 넘을 때 여러 stack으로 정확히 분할
- 클라이언트가 ZDO owner가 아닌 상태에서도 정상 요청
- dedicated server에서 `Player.m_localPlayer` 없이 정상 처리
- ward 비허용, container privacy, 원거리/위조 player ID 요청 차단
- 두 사용자의 동시 요청, preview 후 컨테이너 변경, 효과 중 소유권 이전 시 전체 취소
- Use/AltPlace 재바인딩과 게임패드 검증
- ZenRecycle 또는 IncineratorControl 단독/동시 설치 환경에서 alternate 경로 분리 확인

## 최종 판단

두 모드 중 하나를 기반으로 포크하거나 Jötunn을 추가하는 것보다, 현재 RepairRequiresMaterials의 기반 위에 작은 `IncineratorDismantleController`를 새로 만드는 것이 더 clean하고 안전하다.

가져올 것은 다음 세 가지다.

- ZenRecycle의 품질별 누적 투자량 개념과 포털 우회 경계 사례
- IncineratorControl의 per-incinerator owner RPC 및 vanilla animation 재사용 개념
- 현재 프로젝트의 exact-prefab `RepairRecipeCatalog`와 ServerSync/버전 강제 구조

버릴 것은 `OnIncinerate` 전역 대체, coroutine transpiler, `RemoveAll()`, shared-name recipe 검색, `Player.m_localPlayer` 의존, 중간 반올림, 미검증 `AddItem`이다.
