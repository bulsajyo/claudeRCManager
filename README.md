# Claude RC Manager

여러 폴더의 `claude remote-control`(`claude rc`) 세션을 한 곳에서 관리하는 Windows 트레이 프로그램입니다.
재부팅 후 로그인하면 등록해 둔 세션을 창 없이 자동으로 다시 시작합니다.

## 기능

- 세션 추가/편집/삭제, 개별·전체 시작/중지/재시작
- 상태 표시: 연결 여부, 활성 세션 수, PID, 시작 시각
- 세션이 죽으면 자동 재시작 (5초 → 최대 60초 간격)
- Windows 로그인 시 자동 실행 (트레이로 조용히 시작)
- 시작할 때 Claude 로그인 여부 확인, 로그인이 안 돼 있으면 `claude auth login` 안내
- 이 PC에서 `claude rc`를 쓰던 폴더를 찾아 한 번에 가져오기
- 세션별 로그 보기, claude.ai/code에서 열기, 폴더 열기

## 요구 사항

- Windows 10/11
- 실행: 추가 설치 없음 (exe 하나에 .NET 런타임 포함, x64)
- 빌드: .NET 8 SDK
- Claude Code CLI (`claude`가 PATH에 있어야 함), 구독 계정으로 로그인

## 빌드

```
dotnet publish -c Release -o dist
```

`dist\ClaudeRcManager.exe` 하나만 만들어지며(약 63MB), 이 파일만 복사해서 실행하면 됩니다.

## 사용

1. `ClaudeRcManager.exe`를 실행합니다. 처음에는 기존에 `claude rc`를 쓰던 폴더 목록이 뜨니 관리할 폴더를 골라 가져옵니다.
2. 창 아래의 **Windows 로그인 시 자동 실행**을 체크합니다.
3. 목록에서 체크된 세션은 프로그램이 시작될 때 자동으로 시작됩니다.

X 버튼은 트레이로 숨기기만 합니다. 완전히 끄려면 트레이 아이콘 우클릭 → 종료.
매니저를 종료하면 관리 중인 rc 세션도 함께 종료됩니다.

세션을 중지하거나 매니저를 끝낼 때는 Ctrl+C 로 정상 종료시켜, 다시 시작했을 때 그 폴더에 연결돼 있던 세션들이 돌아오도록 합니다.`ClaudeRcManager.exe --exit` 로 실행 중인 매니저를 같은 방식으로 끝낼 수 있습니다.

처음 쓰는 폴더는 터미널에서 `claude`를 한 번 실행해 폴더 신뢰를 해 둬야 합니다.

## 설정 위치

- 설정: `%APPDATA%\ClaudeRcManager\config.json`
- 로그: `%APPDATA%\ClaudeRcManager\logs\`
- 환경 변수 `CLAUDE_RC_MANAGER_HOME`으로 위치를 바꿀 수 있습니다.
