# GodotAudioConsole

Console audio interamente in **C# / .NET 8**, con **Godot 4.6 .NET** come interfaccia.
Il backend audio attuale usa WASAPI e richiede **Windows**.

## Struttura e dipendenze

| Progetto | Responsabilità | Dipendenze |
| --- | --- | --- |
| `libraries/AudioConsole.Core` | Stato applicativo, guadagno, PCM, timer, forma d'onda, cronologia temporanea, selezione, comandi di ascolto ed esportazione WAV | Solo libreria standard .NET |
| `libraries/AudioConsole.Windows` | Microfoni, acquisizione e riproduzione WASAPI, conversione campioni | Core e NAudio.Wasapi 2.2.1 |
| `new-game-project` | Controlli, disegno, finestre, conferme e traduzione dei comandi utente | Core, Windows e Godot |
| `tests/AudioConsole.Tests` | Test applicativi senza motore e prova hardware opzionale | Core e Windows |

Godot crea `AudioConsoleService` passando `IAudioInput` e `IAudioPlayback`, inoltra i comandi e
legge `ConsoleSnapshot`. Gli snapshot sono copie coerenti e possono essere letti
dal thread grafico mentre il thread audio acquisisce. Il motore non gestisce
microfoni, bus audio, guadagno, registrazione, riproduzione o codifica WAV.

Le DLL `AudioConsole.Core.dll` e `AudioConsole.Windows.dll` vengono compilate e
copiate automaticamente insieme all'assembly Godot tramite `ProjectReference`.
Non occorre copiare DLL a mano. Nessuna libreria fa riferimento a GodotSharp.
Per aggiungere un altro sistema operativo si implementano `IAudioInput` e `IAudioPlayback` e si cambia
la composizione nella vista, mantenendo invariata la logica applicativa.

## Modifica della grafica nell'editor

L'interfaccia è definita in risorse Godot, visibili anche senza avviare il progetto:

- `new-game-project/scenes/audio_console.tscn`: layout, etichette, selettore,
  slider, pulsanti, timer, cronologia, comandi di ascolto e finestre di salvataggio/conferma.
- `new-game-project/scenes/waveform_display.tscn`: display riutilizzabile con
  pannello verde, bordo, griglia e linea centrale.
- `new-game-project/themes/console_theme.tres`: font, colori e stili dei pulsanti.

Apri la scena principale nella vista 2D per modificarne i nodi e le proprietà
nell'Inspector. I Container mantengono l'allineamento durante il ridimensionamento.
`AudioConsoleView.cs` recupera i controlli tramite nomi univoci (`%RecordButton`,
ecc.) e collega i segnali: non costruisce l'interfaccia a runtime. Mantieni questi
nomi quando sposti i controlli nell'albero. `WaveformView.cs` disegna soltanto
la traccia audio dinamica nel nodo `Trace` del display.

NAudio.Wasapi è fissato alla versione 2.2.1 per la compatibilità con .NET 8;
la licenza del pacchetto è MIT: [repository NAudio](https://github.com/naudio/NAudio/tree/v2.2.1).

## Compilazione e avvio

Servono Godot **.NET** 4.6.1 e un SDK .NET 8 o successivo. Dalla radice:

```powershell
dotnet restore new-game-project/GodotAudioConsole.sln
dotnet build new-game-project/GodotAudioConsole.sln
```

Apri `new-game-project/project.godot` con Godot .NET e premi **F5**.
La soluzione `new-game-project/GodotAudioConsole.sln` include anche le librerie
esterne e il progetto di test ed è apribile in Visual Studio/Rider.
Il primo restore richiede NuGet per l'SDK Godot e NAudio.

## Utilizzo

- Display verde con forma d'onda nera in tempo reale, anche prima di registrare.
- Selezione microfono e pulsante Aggiorna per rilevare gli ingressi disponibili.
- Volume 0–200% applicato nella libreria: 100% mantiene il livello originale,
  0% registra silenzio; i picchi fuori scala vengono saturati nel PCM16.
- Registra / Stop / Salva WAV, con tempo basato sui campioni effettivamente ricevuti.
- WAV PCM a 16 bit, con frequenza e canali mono/stereo dell'ingresso.
- Lista sotto la console delle ultime **10 registrazioni**, con durata e stato
  **Temporanea / Salvata**. La più recente viene selezionata automaticamente.
- **Ascolta / Ferma ascolto** sulla registrazione selezionata, anche prima di
  esportarla. Il doppio clic su una riga avvia l'ascolto.
- **Salva WAV** esporta la registrazione selezionata, lasciandola nella cronologia.
- Una nuova registrazione conserva le precedenti e ferma l'eventuale ascolto.
- Conferma di uscita se nella sessione rimangono registrazioni non esportate.
- Errori di acquisizione e salvataggio visibili; una disconnessione ferma la
  registrazione e conserva i campioni già acquisiti.

Non viene riprodotto il microfono in diretta sugli altoparlanti. Nessun effetto o Discord.
Il volume è un guadagno software sulla cattura, non modifica il livello del
microfono nelle impostazioni Windows. Durante l'acquisizione i campioni sono in
memoria; a Stop diventano WAV temporanei in una cartella privata della sessione
sotto `%TEMP%/GodotAudioConsole`. La lista è limitata a 10: all'undicesima
registrazione viene eliminata la copia temporanea più vecchia. I file WAV esportati
dall'utente non vengono mai eliminati. Alla chiusura normale
le copie temporanee vengono rimosse e la sessione successiva parte con lista vuota.
La finestra dispone di scorrimento verticale per mantenere accessibili i comandi
anche quando viene ridotta.

Se compare **NESSUN SEGNALE**, parla nel microfono e controlla ingresso, volume
e accesso al microfono per le app desktop nelle impostazioni Privacy di Windows.
Dopo aver collegato/scollegato un ingresso, premi Aggiorna a registrazione ferma.

## Verifica

Test deterministici, senza Godot e senza aprire il microfono:

```powershell
dotnet run --project tests/AudioConsole.Tests
```

Verificano PCM/WAV, guadagno 100/50/0%, saturazione, timer, snapshot, concorrenza,
cronologia e limite di 10 elementi, riascolto tramite backend simulato, selezione,
esportazione dei singoli elementi, pulizia dei file temporanei, conservazione
dei WAV esportati, errori di scrittura e disconnessione dell'ingresso.
Sono un eseguibile di test senza dipendenze da un framework di test esterno;
restituiscono codice 0 quando tutti i controlli passano.

Prova hardware: apre il microfono predefinito per mezzo secondo e salva un WAV:

```powershell
dotnet run --project tests/AudioConsole.Tests -- --hardware
```

Prova della riproduzione WASAPI reale, dopo il test deterministico: riproduce
il WAV di silenzio generato dal test e verifica avvio e fine automatica:

```powershell
dotnet run --project tests/AudioConsole.Tests -- --playback-hardware
```

Test della UI C# (dopo la build, sostituire `godot` con l'eseguibile .NET):

```powershell
godot --headless --path new-game-project res://tests/console_ui_smoke.tscn
```

Usa ingresso e uscita sintetici e verifica cronologia, selezione, ascolto,
salvataggio tramite DLL e conferma di uscita. Senza `--headless` verifica anche il rendering e salva
`.godot/csharp-console-preview.png`. I file temporanei sono in `.artifacts`
o `.godot`, entrambe ignorate da Git.

Per la verifica manuale, seleziona un ingresso, parla, registra cambiando volume,
premi Stop e riascolta dalla lista. Registra più volte senza esportare, seleziona
una voce precedente ed esportala; verifica che all'undicesima rimangano dieci
voci. Prova anche Annulla nella conferma di chiusura.
