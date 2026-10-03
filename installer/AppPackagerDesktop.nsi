; App Packager Desktop per-user installer.
;
; Compiled by tools\build-installer.ps1, which supplies every define below and
; generates the uninstaller's file list from the published stage. Installs
; without elevation under the user's local programs folder so the Definitions
; folder stays writable for definitions the user saves from the application.

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

!ifndef VERSION
  !error "VERSION is required, e.g. /DVERSION=2.2.0"
!endif
!ifndef VERSION4
  !error "VERSION4 is required, e.g. /DVERSION4=2.2.0.0"
!endif
!ifndef STAGE_DIR
  !error "STAGE_DIR is required: the published folder to package"
!endif
!ifndef OUT_FILE
  !error "OUT_FILE is required: the installer path to write"
!endif
!ifndef GENERATED_DIR
  !error "GENERATED_DIR is required: the folder holding shipped-files.nsh"
!endif
!ifndef ICON_FILE
  !error "ICON_FILE is required: the application icon"
!endif

!define PRODUCT_NAME "App Packager Desktop"
!define PRODUCT_PUBLISHER "Jason Ulbright"
!define PRODUCT_EXE "ApplicationPackagerUI.exe"
!define BUILD_EXE "AppPackagerBuild.exe"
!define ARP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AppPackagerDesktop"
!define INSTALL_KEY "Software\AppPackagerDesktop"

Name "${PRODUCT_NAME}"
OutFile "${OUT_FILE}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_NAME}"
InstallDirRegKey HKCU "${INSTALL_KEY}" "InstallDir"
SetCompressor /SOLID lzma
ManifestDPIAware true
BrandingText " "

VIProductVersion "${VERSION4}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION4}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey "LegalCopyright" "MIT License"

!define MUI_ICON "${ICON_FILE}"
!define MUI_UNICON "${ICON_FILE}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetOutPath "$INSTDIR"

  ; A running instance holds its executable open; the copy below would fail
  ; halfway, so the check runs first. Opening for append succeeds on an idle
  ; file and is refused for an executing image. A scheduled command-line build
  ; holds AppPackagerBuild.exe open the same way.
  ${If} ${FileExists} "$INSTDIR\${PRODUCT_EXE}"
    ClearErrors
    FileOpen $0 "$INSTDIR\${PRODUCT_EXE}" a
    ${If} ${Errors}
      ${IfNot} ${Silent}
        MessageBox MB_ICONSTOP "Close ${PRODUCT_NAME} and run the installer again."
      ${EndIf}
      Abort "${PRODUCT_NAME} is running."
    ${EndIf}
    FileClose $0
  ${EndIf}
  ${If} ${FileExists} "$INSTDIR\${BUILD_EXE}"
    ClearErrors
    FileOpen $0 "$INSTDIR\${BUILD_EXE}" a
    ${If} ${Errors}
      ${IfNot} ${Silent}
        MessageBox MB_ICONSTOP "A command-line build (${BUILD_EXE}) is running. Wait for it to finish and run the installer again."
      ${EndIf}
      Abort "${BUILD_EXE} is running."
    ${EndIf}
    FileClose $0
  ${EndIf}

  ; The previous version's uninstaller removes the files it shipped, so a
  ; definition dropped from the catalog does not linger. Files it did not ship,
  ; such as definitions the user saved, are not in its list and stay.
  ${If} ${FileExists} "$INSTDIR\Uninstall.exe"
    ExecWait '"$INSTDIR\Uninstall.exe" /S _?=$INSTDIR'
    Delete "$INSTDIR\Uninstall.exe"
  ${EndIf}

  File /r "${STAGE_DIR}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}"

  WriteRegStr HKCU "${INSTALL_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${ARP_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${ARP_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_EXE}"
  WriteRegStr HKCU "${ARP_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${ARP_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${ARP_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${ARP_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${ARP_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  ; One Delete per shipped file and one RMDir per shipped folder, generated
  ; from the stage at build time. Folders holding user files are left in place
  ; because RMDir without /r refuses a non-empty folder.
  !include "${GENERATED_DIR}\shipped-files.nsh"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}.lnk"
  DeleteRegKey HKCU "${ARP_KEY}"
  DeleteRegKey HKCU "${INSTALL_KEY}"
SectionEnd
