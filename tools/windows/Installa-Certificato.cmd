@echo off
rem Fa riconoscere a Windows la firma di Ponte iPhone su questo PC (solo per l'utente attuale).
rem Windows chiede conferma: rispondere Si.
certutil -user -addstore Root "%~dp0PonteIPhone.cer"
certutil -user -addstore TrustedPublisher "%~dp0PonteIPhone.cer"
echo.
echo Fatto. Ora nelle proprieta di PonteIPhone.exe, scheda Firme digitali, la firma risulta valida.
pause
