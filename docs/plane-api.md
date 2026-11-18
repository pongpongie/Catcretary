# Plane API 연동

[← README](../README.md)

사용하는 엔드포인트, 호출 수 제한, 에러 로그 규격이다.

## 사용하는 Plane 엔드포인트

경로 앞은 `/api/v1/workspaces/{slug}`. 작업 항목은 1.4.2 의 현재 경로인 `work-items/` 를 쓴다(`issues/` 는 같은 뷰의 옛 이름).

| 용도 | 메서드 · 경로 |
|---|---|
| 내 id | `GET /api/v1/users/me/` |
| 멤버 이름(새 소식 작성자 표시) | `GET /members/` |
| 프로젝트 목록 / 생성 | `GET` / `POST /projects/` — 생성은 `{"name", "identifier"}` |
| 상태 목록 / 생성 | `GET` / `POST .../projects/{id}/states/` — 목록은 6시간 캐시 |
| 작업 목록 | `GET .../projects/{id}/work-items/?per_page=100&fields=id,name,sequence_id,priority,target_date,start_date,project,state,assignees,labels,is_draft,updated_at,updated_by` |
| 식별자로 한 건 | `GET /work-items/{KEY}-{N}/` |
| 검색 | `GET /work-items/search/?search=…&workspace_search=true&limit=8` |
| 상태 변경 | `PATCH .../projects/{p}/work-items/{i}/` — `{"state": "<state id>"}` |
| 날짜 변경 (캘린더 끌기) | `PATCH .../projects/{p}/work-items/{i}/` — `{"start_date", "target_date"}` 중 원래 있던 것만 |
| 항목 수정 (보드 카드) | `PATCH .../projects/{p}/work-items/{i}/` — `name`, `priority`, `start_date`, `target_date`(null 이면 지움), `assignees`, `labels` 중 바뀐 것만 |
| 새 항목 | `POST .../projects/{id}/work-items/` — `name`, `priority`, `assignees`, `start_date`, `target_date`, `labels`, 보드 빠른 추가는 `state` 도 |
| 댓글 조회 / 작성 | `GET .../work-items/{i}/comments/?per_page=5` / `POST` `{"comment_html"}` |
| 라벨 조회 / 생성 | `GET` / `POST .../projects/{id}/labels/` |
| 진행 중 cycle | `GET .../projects/{id}/cycles/?cycle_view=current` (페이지 봉투 없이 배열) |
| cycle 에 넣기 | `POST .../cycles/{c}/cycle-issues/` — `{"issues": ["<id>"]}` |
| 프로젝트 멤버 조회 / 추가 | `GET` / `POST .../projects/{id}/project-members/` — 추가는 `{"member", "role"}`. 조회 응답에 role 은 없다 |
| 항목 페이지 열기(웹, API 아님) | `{baseUrl}/{slug}/projects/{projectId}/issues/{issueId}` |

이 self-hosted 빌드에는 workspace 전체를 PQL 로 질의하는 엔드포인트가 없다(404). 그래서 프로젝트별로
조회한 뒤 앱에서 필터한다.

프로젝트 목록은 내가 멤버가 아닌 공개 프로젝트까지 돌려주지만, 그런 프로젝트의 `states`·`issues` 는
403 이다. 그래서 목록 응답의 `is_member` 가 `false` 인 프로젝트는 조회하지 않는다.

페이지(Pages)는 public API(`/api/v1/`)에 없고 웹 앱 내부 API(`/api/…/pages/`, 세션 인증 전용)에만 있어 API 키로는 만들 수 없다.

## 호출 수와 제한

API 키는 분당 60회(서버 `API_KEY_RATE_LIMIT` 기본값)로 제한된다.

| 동작 | 호출 수 |
|---|---|
| 새로고침 1회 | `me`·`members`·`projects` 3 + 프로젝트마다 `work-items`·`cycles` 2 + 댓글 확인 최대 20. states 는 캐시가 있으면 0 |
| 보드·캘린더 쓰기 1회 | 쓰기 1 + 1.5초 뒤 그 프로젝트 `work-items` 다시 받기 1(연달아 쓰면 한 번으로 모은다). 마지막 쓰기 30초 뒤 전체 새로고침 1회 |
| 앱 안 호출 수 관리 | 모든 요청을 1분 창으로 센다. 조회는 48회, 쓰기는 58회까지 보내고 넘으면 자리가 날 때까지 기다린다(조회가 한도를 다 써도 쓰기 몫이 남는다). 조회 몫이 8회 미만이면 댓글 확인을 멈추고 다음 새로고침으로 미룬다. 대기는 `INFO` 로그 |
| 429 응답 | `Retry-After` 가 60초 이하면 그만큼 기다렸다가 한 번 더 보낸다. 댓글 확인 중 두 번째도 429 면 남은 항목은 다음 새로고침으로 미룬다 |

## 에러 로그

| 항목 | 내용 |
|---|---|
| 파일 | `%APPDATA%\Catcretary\logs\yyyy-MM-dd.log`, UTF-8. 시작할 때 수정일이 7일 지난 파일 삭제 |
| 형식 | `yyyy-MM-dd HH:mm:ss.fff ERROR <상황>` 다음 줄부터 예외 전체(stack trace 포함) |
| Plane 실패 | `PlaneApiException`. 화면 메시지 `GET <경로(쿼리 제외)> → 403 Forbidden`, 로그에 쿼리 포함 요청 경로와 응답 본문 앞 500자 추가. `x-api-key` 는 남기지 않는다 |
| 남기는 곳 | 새로고침, 등록, 완료 처리, 상태 변경, 댓글, 검색, 프로젝트 생성·템플릿 복사(항목별), cycle 넣기·조회, 댓글 확인, 페이지 열기, 연결 테스트, 설정·상태·스프라이트 manifest 읽기 실패. 429 재시도는 `INFO` |
| 전역 | `DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` 도 기록. 기록만 하고 종료 동작은 바꾸지 않는다 |
