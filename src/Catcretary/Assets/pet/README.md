# 고양이 스프라이트

이 폴더의 `*.png` 는 저장소에 넣지 않는다(`.gitignore`). 에셋 라이선스가 재배포를 금지하기 때문이다.
파일이 없으면 앱은 코드로 그린 임시 고양이를 쓴다.

## 추천 에셋

| 에셋 | 셀 | 애니메이션 | 라이선스 | 비고 |
|---|---|---|---|---|
| [2D Pixel Art Cat Sprites — Elthen](https://elthen.itch.io/2d-pixel-art-cat-sprites) | 32×32 | idle×2, clean×2, movement×2, sleep, paw, jump, scared | 개인·상업 사용 가능, 수정 가능, **재배포·재판매 금지**, 크립토/AI 학습 금지. 크레딧 선택 ([라이선스](https://www.patreon.com/elthen/posts/licensing-27430241)) | 상태 5개를 다 덮는 유일한 무료 팩. 기본 선택 |
| [Tiny Kitten — OpenGameArt](https://opengameart.org/content/tiny-kitten-game-sprite) | 미확인 | idle, run, jump up/fall, hurt, dead | CC0 | 라이선스가 가장 깨끗하지만 sleep/scared 가 없음 |
| [16 bit kitty free — Maze.Bit.Boutique](https://mxmaze.itch.io/16-bit-kitty-free) | 16×16 | idle, walk, sit, sleep | CC BY 4.0 | 작아서 8배 확대 필요 |

## 넣는 방법 (Elthen 기준)

1. itch.io 페이지에서 `Download Now` → `No thanks, just take me to the downloads` → PNG 저장.
2. 이 폴더에 `Cat Sprite Sheet.png` 라는 이름 그대로 둔다 (`manifest.json` 이 이 이름을 가리킨다).
3. 앱을 다시 실행하면 임시 고양이 대신 스프라이트가 뜬다.

### 시트 구조 (256×320, 32px 셀, 10행)

| 행 | 애니메이션 | 프레임 | 앱에서 쓰는 상태 |
|---|---|---|---|
| 0 | idle | 4 | `Idle` (평소) |
| 1 | idle 2 | 4 | `Curious` (마우스 올림, 가끔) |
| 2 | clean | 4 | `Groom` (가끔, 내려놓은 직후) |
| 3 | clean 2 | 4 | `Thinking` (Plane 호출 중) |
| 4 | walk | 8 | `Walk` (산책. 시트는 오른쪽을 보므로 `facing: right`, 왼쪽으로 갈 때 반전) |
| 5 | run | 8 | — |
| 6 | sleep | 4 | `Sleep` (6분 무조작) |
| 7 | paw | 6 | `Listening` (입력·확인 말풍선 열림) |
| 8 | jump | 7 | `Done` (등록·완료 직후, 한 번 재생) |
| 9 | scared | 8 | `Alert` (마감 지난 항목 있음), `Scared` (드래그 중) |

다른 행을 쓰고 싶으면 `manifest.json` 의 `row`/`frames`/`fps` 만 바꾼다. 시트가 왼쪽을 보고 있으면 `"flip": true`.
