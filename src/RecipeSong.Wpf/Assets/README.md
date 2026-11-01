# RecipeSong 로고

겹친 음반 슬리브와 레코드로 음악을 고르고 모으는 앱을 표현한다. 차콜 `#181B1C`, 아이보리 `#F0EADB`, 구리색 `#CC7858`을 사용한다.

`logo.svg`가 원본이며 저장소 루트에서 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-logo.ps1`로 `logo.png`와 `app.ico`를 만든다. 실행 정책은 이 프로세스에만 적용하며 사용자 설정은 바꾸지 않는다. 별도 그림 변환기는 필요 없다.

PNG는 화면과 WPF 창 아이콘에, 16~256px 9개 크기를 담은 ICO는 실행 파일과 설치 바로가기에 사용한다. WPF의 `Window.Icon`은 PNG를 유지한다.
