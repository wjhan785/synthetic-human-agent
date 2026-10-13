# Synthetic Human Agent  —  run guide

**Download the .exe file from the releases on the right**

## DESCRIPTION
  A single Windows .exe that simulates a human office worker for ~15 minutes:
  it edits an OpenOffice Calc spreadsheet and a Writer document, and browses
  several websites — with human-like timing, typing, and curved mouse motion.
  Launched apps appear under explorer.exe (not under this agent), and every
  keystroke/click is genuine input (no automation drivers or flags).

## REQUIREMENTS ON THE VM
  - Apache OpenOffice 4.x  (default path: C:\Program Files (x86)\OpenOffice 4)
      * Open it once manually to clear the first-run "Welcome" wizard, then close it.
      * Quit the OpenOffice Quickstarter if it sits in the system tray
        (right-click its icon -> Exit) — otherwise office tasks get skipped.
  - Google Chrome
  OpenOffice and Chrome are detected automatically. If either is missing, that
  activity is simply skipped (the rest still run).

## HOW TO RUN
  1. Download and copy SyntheticAgent.exe onto the Desktop.
  2. Double-click it FROM EXPLORER (this is what roots everything under explorer.exe).
  3. In the dialog:  Yes = 15-minute run,  No = 5-minute run,  Cancel = don't start.
  4. Close your other windows first and DO NOT touch the mouse/keyboard while it runs.
  5. EMERGENCY STOP at any time:  Ctrl + Alt + Shift + Q

## WHAT IT PRODUCES  (in a "test-output" folder created next to the .exe)
  - run-YYYYMMDD-HHMMSS.log   timestamped action log + the RNG seed (reproducible)
  - calc_demo.ods            the spreadsheet it created (table + two =SUM formulas)
  - writer_demo.odt          the memo it typed
  - launched-pids.txt        every process the agent launched

## SELF-AUDIT (the "we red-teamed ourselves" report)
  Run:   SyntheticAgent.exe audit
  It writes test-output\self-audit-*.md — delay histogram + autocorrelation
  (no fixed-interval timing), process-authenticity notes, and a command-line
  flag scan (no scripting/automation tells).

## NOTES
  - Self-contained: no .NET install needed on the VM.
  - It is a GUI-subsystem exe, so no console window appears; watch the apps and
    the run-*.log for progress.
  - In the test range (TLS-intercepting proxy) the agent clicks through the
    browser's "your connection isn't private" page automatically.
  - Reproduce a specific run from its logged seed:  SyntheticAgent.exe run 15 <seed>
