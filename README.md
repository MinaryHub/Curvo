# Curvo

굴곡진 벽면·기둥·아치에 프로젝터를 투사할 때 화면을 실시간으로 **역왜곡(warping)** 해서,
실제 벽면에서 직선이 직선으로 보이도록 맞춰 주는 Windows 데스크톱 유틸리티입니다.

*A Windows utility that pre-warps the screen in real time for projection onto curved walls,
pillars and arches.*

- 동영상·PPT·PDF·이미지를 앱에서 바로 재생하거나, 실행 중인 창을 캡처해 투사
- 베지어 곡면 + 코너 핀 보정, 색보정·블렌딩
- 디코딩·캡처·렌더링 전 구간 GPU 상주 (Direct3D 11, Windows.Graphics.Capture)

소스·빌드 방법·사용법은 [`ProjectorWarp/`](ProjectorWarp/) 폴더에 있습니다.

- [README](ProjectorWarp/README.md) — 동작 방식, 요구 사항, 빌드·실행
- [SETUP.md](ProjectorWarp/docs/SETUP.md) — 설치와 설정
- [CALIBRATION.md](ProjectorWarp/docs/CALIBRATION.md) — 보정 방법

## 후원

Curvo는 [MinaryHub](https://github.com/MinaryHub)가 만듭니다. 도움이 됐다면
[후원](https://github.com/sponsors/MinaryHub)으로 작업을 응원해 주세요 — 잠긴 기능은 없습니다.

## 라이선스

MIT — [LICENSE](LICENSE)
