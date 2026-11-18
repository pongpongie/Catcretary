# 설정 파일

[← README](../README.md)

API 키가 들어가므로 저장소가 아니라 `%APPDATA%\Catcretary\config.json` 에 둔다.

| 필드 | 의미 |
|---|---|
| `baseUrl` | Plane 서버 주소 |
| `workspaceSlug` | 워크스페이스 slug |
| `apiKey` | Plane API 키. 이 키의 주인이 "나" 다 |
| `briefingTime` | 알림 시각, `HH:mm` |
| `weekdaysOnly` | 평일에만 알림 |
| `excludedProjects` | 브리핑·등록 대상에서 뺄 프로젝트 identifier 목록 |
| `projectAliases` | identifier → 별칭 배열. 예: `"SHOP2": ["SHOP", "쇼핑몰"]`. 말로 만든 프로젝트의 별칭도 여기 저장된다 |
| `petLeft`, `petTop` | 캐릭터 위치(DIP). 드래그하면 자동 저장 |
| `petScale` | 스프라이트 확대 배율 (32px × 4 = 128px) |
| `defaultProject` | 말로 등록할 때 프로젝트를 말하지 않으면 넣을 프로젝트 identifier. 보드 "모든 프로젝트" 보기의 빠른 추가에서도 처음 고른 프로젝트가 된다. 비우면 되묻는다. 설정 창에서 고른다 |
| `refreshIntervalMinutes` | 백그라운드 새로고침 주기(분, 최소 5). 기본 30 |
| `openWindowOnBriefing` | 예약 필드. 현재 동작에 영향 없음 |
| `widget.visible` | 위젯 표시 여부 |
| `widget.left`, `widget.top`, `widget.width`, `widget.height` | 위젯 위치·크기(DIP). 옮기거나 크기를 바꾸면 자동 저장. `null` 이면 주 모니터 우상단 560×480 |
| `widget.locked` | 위치·크기 고정 |
| `widget.alwaysOnTop` | `false` 바탕화면 층(기본), `true` 다른 창 위 |
| `widget.theme` | `light` / `dark` / `system` |
| `widget.transparency` | 배경 투명도 0~100(%) |
| `widget.fontSize` | 기준 글자 크기 |
| `widget.weekStartsMonday` | 주 시작을 월요일로 |
| `widget.showLunar`, `widget.showHolidays`, `widget.showRange` | 음력 / 공휴일 / 시작일~마감일 기간 표시 |
| `widget.focusWeek` | `true` 면 이번 주를 가운데 둔 5주 보기 |
| `widget.showCycles` | 진행 중인 cycle 기간 띠 표시. 기본 `true` |
| `widget.scope` | `mine` 내 담당(기본) / `all` 전체 |
| `widget.projects` | 볼 프로젝트 identifier 배열. 비면 모든 프로젝트 |
| `board.visible` | 보드 위젯 표시 여부. 기본 `false` |
| `board.locked`, `board.alwaysOnTop`, `board.theme`, `board.transparency` | 의미는 `widget.*` 과 같다. 기본 `false` / `false` / `light` / `15` |
| `board.left`, `board.top`, `board.width`, `board.height` | 보드 위치·크기. 없으면 주 모니터 작업 영역 가운데 1180×660(작업 영역보다 크면 줄임) |
| `board.scope` | `mine` / `all`(기본) |
| `board.project` | 프로젝트 identifier. 빈 문자열이면 모든 프로젝트(열 = state 그룹) |
| `board.showClosed` | 완료·취소 카드 표시. 기본 `false` |
| `widget.extraHolidays` | 계산할 수 없는 휴일. `"2026-06-03": "지방선거"` 형식 |

`%APPDATA%\Catcretary\state.json` 은 앱이 쓰는 기록이다.

| 필드 | 의미 |
|---|---|
| `lastBriefingKey` | 마지막으로 브리핑을 띄운 `날짜@시각` |
| `baselineIds` | "새로 배정" 비교 기준. 지난 브리핑 때의 내 담당 id |
| `announcedIds` | 이번 브리핑 때의 내 담당 id. 다음 브리핑에서 `baselineIds` 가 된다 |
| `lastAnnounceAt`, `prevAnnounceAt` | 브리핑 시각(UTC). `prevAnnounceAt` 이후의 댓글·변경을 "새 소식" 으로 본다 |
