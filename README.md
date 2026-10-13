# Ponte iPhone

App per Windows che legge foto e video dell'iPhone collegato via USB e li **copia** o **sposta** sul PC,
in cartelle Anno\Mese, con verifica di ogni file.

## Cosa serve

- Windows 10 o 11 a 64 bit
- L'app gratuita **Dispositivi Apple** (Microsoft Store) oppure iTunes: installano il driver USB dell'iPhone
- Al primo collegamento: sbloccare l'iPhone e toccare **Autorizza**
- Consigliato, sull'iPhone: Impostazioni › App › Foto › Trasferisci su Mac o PC › **Mantieni originali**

## Come funziona

- Si collega all'iPhone con Windows Portable Devices (WPD), la stessa via di Esplora file e dell'app Foto.
  Il dialogo con il telefono gira in un **processo separato sorvegliato**: se il driver si blocca (succede con i
  video grandi), il processo viene riavviato e il file riparte da capo, senza bloccare l'app.
- Ogni file arriva in un file temporaneo con impronta SHA-256, viene riletto dal disco e solo dopo prende il nome
  definitivo. Un file esistente non viene mai sovrascritto.
- **Sposta** cancella dal telefono alla fine, e solo gli elementi verificati sul PC. Se iOS non permette la
  cancellazione da Windows, le copie restano e l'app lo dice.
- Live Photo, versioni modificate (IMG_E…) e file .AAE sono raggruppati come nell'app Foto.
- La data della cartella Anno\Mese si legge dal file (EXIF, data Apple dei video con fuso orario).
- Conversione HEIC → JPG facoltativa, con ImageMagick incluso (non servono estensioni dello Store).
- Registro delle copie (SQLite): bollino "✓ PC" e filtro "Da copiare", anche dopo mesi.

## Sviluppo

- `src/PonteIPhone.Core` — libreria, motore di copia, registro, protocollo con il processo separato (provato con un telefono finto)
- `src/PonteIPhone.Wpd` — collegamento WPD all'iPhone
- `src/PonteIPhone` — app WPF (stile Windows 11 con WPF-UI)
- `tests/PonteIPhone.Tests` — test del motore e del watchdog con guasti simulati

Si compila solo con GitHub Actions: ogni ramo esegue i test, compila e salva le schermate dell'app in modalità
dimostrativa (`PonteIPhone.exe --demo`); su `main` l'eseguibile viene firmato e pubblicato come Release.
L'app si aggiorna da sola dalle Release, controllando la firma con il certificato incorporato.
