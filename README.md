# GodotAudioConsole

Console audio interamente in **C# / .NET 8**, con **Godot 4.6 .NET** come interfaccia.
Il backend audio attuale usa WASAPI e richiede **Windows**.

## Struttura e dipendenze

| Progetto | Responsabilità | Dipendenze |
| --- | --- | --- |
| `libraries/AudioConsole.Core` | Mixer multisorgente, EQ, Echo, banco Loop, ultimo setup JSON, guadagni, buffer/clock, PCM, timeline, ritaglio, cronologia, ascolto ed esportazione WAV | Solo libreria standard .NET |
| `libraries/AudioConsole.Windows` | Microfoni, cattura dell'uscita PC (loopback), riproduzione WASAPI, conversione campioni | Core e NAudio.Wasapi 2.2.1 |
| `new-game-project` | Controlli, disegno, finestre, conferme e traduzione dei comandi utente | Core, Windows e Godot |
| `tests/AudioConsole.Tests` | Test applicativi senza motore e prova hardware opzionale | Core e Windows |

Godot crea `AudioConsoleService` passando `IAudioInput`, `IAudioPlayback` e `IAudioRedirect`, inoltra i comandi e
legge `ConsoleSnapshot`. Gli snapshot sono copie coerenti e possono essere letti
dal thread grafico mentre il thread audio acquisisce. Il motore non gestisce
microfoni, bus audio, guadagno, registrazione, riproduzione o codifica WAV.
Il client chiama `Update()` sul thread dei comandi per gestire la fine della
riproduzione e il ripristino dell'ingresso; `GetSnapshot()` legge lo stato senza
aprire o chiudere dispositivi.
L'app usa `MultiSourceAudioInput`: una factory crea un backend normalizzato per ogni
dispositivo; `StereoMixer` somma i frame su un'unica timeline stereo a 48 kHz.
`ConfigureSources()` aggiunge/rimuove sorgenti a riposo, `SetSourceVolume()` modifica
il guadagno di una sorgente anche durante la registrazione. Gli identificativi
canonici impediscono di aggiungere due volte lo stesso endpoint tramite un alias predefinito.

Le DLL `AudioConsole.Core.dll` e `AudioConsole.Windows.dll` vengono compilate e
copiate automaticamente insieme all'assembly Godot tramite `ProjectReference`.
Non occorre copiare DLL a mano. Nessuna libreria fa riferimento a GodotSharp.
Per aggiungere un altro sistema operativo si implementano `IAudioInput` e `IAudioPlayback` e si cambia
la composizione nella vista, mantenendo invariata la logica applicativa.

## Modifica della grafica nell'editor

L'interfaccia è definita in risorse Godot, visibili anche senza avviare il progetto:

- `new-game-project/scenes/audio_console.tscn`: layout, etichette, selettore,
  slider, pulsanti, timer, cronologia, pannello effetti laterale, comandi di ascolto e finestre di salvataggio/conferma.
- `new-game-project/scenes/waveform_display.tscn`: display riutilizzabile con
  pannello verde, bordo, griglia e linea centrale.
- `new-game-project/scenes/recording_editor.tscn`: timeline selezionabile, secondo
  timer, livelli L/R, campi di inizio/fine e pulsante di ritaglio.
- `new-game-project/scenes/audio_source_strip.tscn`: riga riutilizzabile per una
  sorgente attiva, con nome, livello, volume e rimozione.
- `new-game-project/scenes/equalizer_panel.tscn` ed `echo_panel.tscn`: controlli
  degli effetti ospitati nel pannello destro, modificabili nelle rispettive scene.
- `new-game-project/scenes/loop_panel.tscn`: registrazione clip, lista Loop,
  riproduzione, volume, pausa e salvataggio nella colonna accanto a Effetti.
- `new-game-project/themes/console_theme.tres`: font, colori e stili dei pulsanti.

Apri la scena principale nella vista 2D per modificarne i nodi e le proprietà
nell'Inspector. I Container mantengono l'allineamento durante il ridimensionamento.
`AudioConsoleView.cs` recupera i controlli tramite nomi univoci (`%RecordButton`,
ecc.) e collega i segnali. Le righe delle sorgenti istanziano la scena già disegnata
`audio_source_strip.tscn`; layout e controlli restano risorse modificabili nell'editor. Mantieni questi
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

Equalizzatore ed Echo sono nel pannello **Effetti** a destra, con scorrimento
indipendente. **×**, **Esc** o il pulsante **Effetti** nell'intestazione chiudono
il pannello, restituendo spazio alla console; lo stesso pulsante lo riapre e
mostra quanti effetti sono attivi. Chiudere il pannello mantiene impostazioni,
effetti e invio audio. Nelle finestre strette il pannello si apre sopra la parte
destra della console; anche un clic sullo sfondo oscurato lo chiude. Intestazione
e pulsante Effetti restano visibili mentre scorri la console.

- Display verde con forma d'onda nera in tempo reale, anche prima di registrare.
- Selettore **Sorgente**: scegli **Microfono** oppure **Audio del PC**, poi un
  dispositivo e premi **+**. Ripeti per aggiungere altri dispositivi al mix.
  Cambiare questo filtro non sostituisce le sorgenti già attive. Il primo
  microfono disponibile viene aggiunto automaticamente all'avvio.
- **Sorgenti attive** mostra ogni ingresso con indicatore di livello e volume
  0–200% indipendenti. **×** rimuove una sorgente a registrazione ferma;
  con zero sorgenti il comando REC è disabilitato. I duplicati vengono rifiutati.
  **Aggiorna** rilegge i dispositivi disponibili.
- **Audio del PC** usa WASAPI loopback e registra il mix riprodotto sull'uscita
  selezionata, comprese le applicazioni che la utilizzano. Non occorrono Mix Stereo
  o cavi audio virtuali. Le uscite 5.1/7.1 vengono convertite in stereo da Windows.
  Se un'app riproduce su un'altra uscita, seleziona quella nel menu.
- **Volume mix** applica il guadagno finale dopo la somma delle sorgenti.
  Riduci i volumi se compare PICCO: la somma non viene normalizzata automaticamente;
  i campioni fuori scala vengono saturati nel PCM16.
- Registra / Stop / Salva WAV, con tempo basato sui campioni effettivamente ricevuti.
- Un unico WAV PCM stereo a 16 bit / **48 kHz** con tutte le sorgenti simultanee.
  Windows converte frequenza e canali di ogni dispositivo prima del mixer.
- Lista sotto la console delle ultime **10 registrazioni**, con durata e stato
  **Temporanea / Salvata**. La più recente viene selezionata automaticamente.
- **Tasto destro** su una voce: menu contestuale con Riproduci / Pausa / Riprendi,
  Ferma riproduzione, Salva WAV e Genera parte selezionata. Il clic destro seleziona
  la voce; i comandi non disponibili sono disabilitati. Il `PopupMenu` è definito
  nella scena principale e modificabile nell'editor.
- Due pulsanti di ascolto sulla registrazione selezionata: **▶ / ⏸** per
  riprodurre, mettere in pausa e riprendere dallo stesso punto, **■** per fermare.
  Il doppio clic su una riga avvia l'ascolto anche prima di esportarla.
- Registrazione con **● rosso** e arresto con **■**. I tooltip spiegano ogni icona;
  gli SVG sono risorse modificabili in `new-game-project/icons` e assegnate nella scena.
- Durante il riascolto e la sua pausa, tutte le catture vengono chiuse e il display
  mostra **MIX SOSPESO** (o MIC/PC per una sola sorgente). A STOP, a fine file o se la
  riproduzione fallisce, tutte le sorgenti vengono riattivate con gli stessi volumi:
  il loopback non registra la riproduzione della console stessa.
- Il display della registrazione mostra la timeline completa, il cursore e un
  secondo tempo **posizione / durata**, oltre a due barre di livello L/R. La
  posizione viene dal clock di uscita; pausa e ripresa non contano il tempo fermo.
- Trascina sulla timeline per scegliere un intervallo, anche da destra a sinistra,
  oppure inserisci **Da / a** in secondi. **Genera parte selezionata** crea una nuova
  voce `Ritaglio` con i campioni selezionati, senza modificare o sostituire l'originale.
  Il pulsante resta disabilitato con una selezione vuota o durante la registrazione.
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

Anche i ritagli contano nel limite di 10 elementi. Quando la lista è piena,
generare un ritaglio conserva la sua sorgente e rimuove la voce più vecchia tra
le altre. I tagli sono allineati ai frame PCM e mantengono frequenza, canali e
campioni originali. Questa versione crea ritagli separati; non unisce ancora più parti.

Se compare **NESSUN SEGNALE**, parla nel microfono e controlla ingresso, volume
e accesso al microfono per le app desktop nelle impostazioni Privacy di Windows.
Dopo aver collegato/scollegato un ingresso, premi Aggiorna a registrazione ferma.
In modalità Audio del PC, avvia un suono sull'uscita scelta per controllare la
forma d'onda. Il cambio di modalità e dispositivo è bloccato durante registrazione
e riascolto; cambiarli a riposo mantiene cronologia, ritagli e registrazioni esportate.
Il backend mantiene attivo un flusso di silenzio sull'uscita acquisita, così anche
le pause senza audio conservano la loro durata nel WAV. Non cambia il volume di Windows.
Il mixer usa un buffer comune di 120 ms e mantiene un clock unico indipendente
dal numero di sorgenti. All'avvio emette silenzio mentre riempie i buffer, senza
accorciare la durata del file. Un errore di una sorgente ferma la registrazione e
conserva quanto acquisito; rimuovi la sorgente segnalata per riprendere con le altre.
Questa versione salva un mix unico, senza tracce separate per ciascuna sorgente.

## Ultimo setup

L'app salva automaticamente la configurazione in:

```text
%LOCALAPPDATA%\GodotAudioConsole\last-setup.json
```

Il JSON contiene versione del formato, sorgenti attive (tipo, nome e identificativo
del dispositivo), volume di ogni sorgente e volume finale del mix. Le modifiche
vengono salvate dopo 0,4 secondi senza ulteriori cambiamenti; la chiusura dell'app
salva subito eventuali modifiche ancora in attesa. La scrittura usa un file
temporaneo e sostituisce il precedente solo quando è completa.

All'avvio vengono ripristinati sorgenti e volumi, senza avviare REC. Anche una
configurazione con zero sorgenti viene rispettata. I dispositivi non disponibili
o che non si aprono vengono segnalati, ripristinando quelli funzionanti. Il setup
salvato non viene riscritto dal solo ripristino parziale: resta disponibile per un
successivo avvio con i dispositivi ricollegati. Una successiva modifica manuale
salva invece la configurazione effettiva della sessione corrente.

Un file corrotto o di versione non supportata viene segnalato e l'app parte con
la configurazione iniziale; il file resta intatto finché non modifichi il setup.
Le registrazioni temporanee e la cronologia non sono incluse nel JSON.
Lettura, validazione, scrittura e risoluzione dei dispositivi sono in
`AudioConsole.Core/LastSetupStore.cs`; Godot collega soltanto avvio, timer e chiusura.

## Equalizzatore della voce e del mix dal vivo

Il pannello statico **Equalizzatore • Mix live** offre bassi (shelf 160 Hz), medi
(picco 1 kHz, Q 0.7), alti (shelf 4 kHz) e **Uscita EQ**, tutti da -12 a +12 dB.
Con **Attivo** abiliti l'effetto; disattivalo per confrontare il suono originale.
**Reset** riporta i quattro valori a 0 dB senza cambiare l'attivazione.

Per provare la voce senza registrare: lascia solo il microfono tra le sorgenti,
attiva l'EQ e avvia **Mix dal vivo** nel pannello di invio. Usa il cavo virtuale
come microfono nell'altra applicazione, oppure scegli delle cuffie come uscita
per ascoltarti. Con più sorgenti l'EQ modifica l'intero mix, compreso l'audio PC.
I comandi sono modificabili durante l'invio e durante REC.

Il percorso è **sorgenti → volumi sorgenti → somma → EQ → Echo → volume mix →
controllo picchi e saturazione → invio / nuova registrazione**. Se compare PICCO,
riduci Uscita EQ o Volume mix. Il volume di uscita dell'EQ è escluso in bypass.
Le registrazioni già presenti conservano il loro suono, anche durante l'ascolto
o l'invio del file: non vengono equalizzate una seconda volta.

L'elaborazione è in `AudioConsole.Core/VoiceEqualizer.cs`, senza Godot o NAudio.
Le formule dei filtri biquad seguono l'[Audio EQ Cookbook del W3C](https://www.w3.org/TR/audio-eq-cookbook/).
Ogni canale ha stati separati; le regolazioni e il bypass effettuano transizioni
graduali, e lo stato dei filtri viene azzerato alla riapertura degli ingressi.
Il pannello è modificabile in `new-game-project/scenes/equalizer_panel.tscn`.
Tutti i valori e l'attivazione vengono salvati nel last setup; i vecchi JSON senza
EQ restano compatibili e vengono caricati con EQ disattivato e valori a zero.

## Echo sul mix dal vivo

Il pannello **Echo • Mix live** aggiunge ripetizioni dopo l'EQ e prima del volume
finale. Puoi usarlo insieme all'equalizzatore, anche inviando al microfono virtuale
senza premere REC. Con più sorgenti attive l'effetto agisce sull'intero mix.

- **Attivo** abilita l'effetto; disattivalo per il bypass.
- **Ritardo**, da 40 a 1000 ms, determina la distanza tra le ripetizioni.
- **Feedback**, da 0 a 85%, determina quanto dura la coda: a zero c'è una sola
  ripetizione; aumentando il valore le ripetizioni decadono più lentamente.
- **Quantità**, da 0 a 100%, regola le ripetizioni aggiunte al suono originale.
- **Reset** ripristina 250 ms, feedback 35% e quantità 30%, mantenendo Attivo.

I parametri si possono cambiare durante invio e registrazione. Quantità, feedback
e bypass hanno transizioni graduali; il cambio di ritardo sfuma tra due letture
del buffer. Il buffer è limitato a un secondo per canale e la coda viene svuotata
dopo il bypass completo e alla riapertura delle catture, evitando di riprodurre
vecchi frammenti dopo il riascolto di un file.

Le nuove registrazioni contengono l'effetto, i file già presenti restano invariati.
STOP REC chiude subito la registrazione: aspetta la fine della coda prima di
premere STOP se vuoi conservarla nel WAV. Se compare PICCO riduci Volume mix.
Attivazione e regolazioni sono nel last setup; un vecchio setup senza Echo viene
caricato con l'effetto disattivato. L'invio non si avvia automaticamente.

L'elaborazione è in `AudioConsole.Core/VoiceEcho.cs`, senza dipendenze da Godot;
il layout statico è in `new-game-project/scenes/echo_panel.tscn`.

## Banco Loop: registra una clip e ripetila insieme alla voce

La colonna **Loop** è separata da **Effetti**. La finestra iniziale è larga 1660 px
per mostrare console, Effetti e Loop. Puoi chiudere ciascuna colonna con **×** e
riaprirla dal pulsante nell'intestazione: l'audio continua. Nelle finestre meno
larghe si apre una colonna per volta, e nelle più strette si sovrappone alla
console. **Esc** o un clic sullo sfondo oscurato chiudono i pannelli aperti.

1. Aggiungi il microfono o le sorgenti desiderate al mix.
2. Nella colonna Loop premi **●**, parla e premi **■** accanto al pulsante REC:
   la nuova clip compare nella lista dedicata, senza aggiungere una registrazione
   alla cronologia principale.
3. Seleziona una clip e imposta il suo **Volume** (0–200%) e la **Pausa** (0–300 s).
   La pausa è l'attesa tra fine clip e nuova ripartenza: con clip da 3 s e pausa
   da 2 s un nuovo giro parte ogni 5 s. **0 s** significa ripetizione continua.
4. Premi **▶** nella colonna Loop. Il microfono resta attivo: voce e loop vengono
   sommati, con guadagno separato per il loop.
5. Per ascoltare o trasmettere scegli **Mix dal vivo** nel pannello di invio e
   avvia **▶** verso cuffie oppure CABLE Input; nell'altra app usa CABLE Output.
   **■** nella colonna Loop ferma soltanto la clip, lasciando passare la voce.

La lista contiene **al massimo 10 clip temporanee per sessione**; l'undicesima
sostituisce la più vecchia. Ogni clip conserva volume e pausa durante la sessione.
**Salva WAV** conserva una copia esterna: la pulizia dei temporanei non la elimina.
Alla chiusura l'app segnala anche le clip Loop non esportate. Le clip non vengono
ricaricate all'avvio; il last setup salva i valori di volume e pausa, senza audio
e senza avviare registrazioni, loop o invio.

REC Loop acquisisce le sorgenti live **dopo EQ/Echo e prima del Volume mix**;
all'avvio ferma il loop precedente e non lo registra nella nuova clip. La clip
viene riprodotta dopo gli effetti, evitando di applicarli di nuovo a ogni giro.
Il Volume mix regola poi voce e loop insieme. REC della console principale può
registrare il risultato completo, anche mentre il loop gira. Un riascolto di file
o l'invio di una registrazione ferma il loop; tornando al live non riparte da solo.

Il player usa il clock dei campioni, non un timer grafico; supporta mono/stereo,
ricampionamento quando cambia il formato dell'ingresso, transizioni ai bordi e
regolazioni del volume graduali. Un cambio della pausa vale dal giro successivo.
La logica è in `AudioConsole.Core/LoopBank.cs` e `AudioConsoleLoops.cs`.

## Invio a un microfono virtuale

Il pannello **Invia a un microfono virtuale** offre due modalità alternative:

- **Mix dal vivo**: invia tutte le sorgenti attive, con i rispettivi volumi e il
  volume mix finale. Puoi anche registrare il mix mentre lo invii.
- **Registrazione selezionata**: invia dall'inizio la voce selezionata nella lista,
  anche temporanea o ritagliata. Usa il volume già presente nel file. Gli ingressi
  vengono sospesi fino a STOP o fine file; timeline, tempo e livelli seguono l'invio.

Scegli una destinazione e premi **▶** nel pannello di invio; **■** interrompe.
L'ascolto locale interrompe l'invio. Cambiare registrazione o generare un ritaglio
ferma l'eventuale invio del file precedente. Un errore dell'uscita non interrompe
una registrazione del mix in corso. Destinazione e modalità sono incluse nel
last setup, ma l'invio **non riparte automaticamente** all'apertura.

La console scrive su un'uscita audio Windows: per presentarla come microfono
ad altre applicazioni occorre un dispositivo virtuale. Configurazione con
[VB-CABLE, sito e istruzioni ufficiali](https://vb-audio.com/Cable/):

1. Scarica il pacchetto dal sito ufficiale, estrailo e avvia l'installer adatto al
   sistema come amministratore. Installa il driver e riavvia Windows come indicato
   dal produttore. L'app non installa driver né modifica i dispositivi predefiniti.
2. Nella console premi **Aggiorna** nel pannello di invio e scegli **CABLE Input**.
3. Nell'app che deve ricevere l'audio scegli **CABLE Output** come microfono.
4. Scegli **Mix dal vivo** oppure **Registrazione selezionata** e premi **▶**.
   Per parlare insieme all'audio del PC, usa il mix dal vivo con microfono e
   loopback dell'uscita fisica sulla quale ascolti il PC.

Non aggiungere CABLE Output al mix che invii a CABLE Input. La console rifiuta
il loopback diretto dell'uscita di invio e le coppie riconosciute di cavi virtuali;
collegamenti esterni complessi (per esempio routing aggiuntivo in VoiceMeeter)
restano responsabilità della configurazione esterna. Le uscite fisiche sono
disponibili anche per instradare audio verso un dispositivo diverso: non diventano
microfoni senza un collegamento virtuale. Non viene selezionata automaticamente
un'uscita alternativa quando quella salvata manca.

## Verifica

Test applicativi, senza Godot e senza aprire dispositivi audio:

```powershell
dotnet run --project tests/AudioConsole.Tests
```

Verificano PCM/WAV, guadagno 100/50/0%, saturazione, timer, snapshot, concorrenza,
cronologia e limite di 10 elementi, riascolto tramite backend simulato, selezione,
esportazione dei singoli elementi, pulizia dei file temporanei, conservazione
dei WAV esportati, sospensione/ripristino del microfono, posizione e livelli,
selezione inversa, ritagli PCM esatti e originali invariati anche con lista piena,
errori di scrittura e disconnessione dell'ingresso.
Verificano anche somma stereo, guadagni separati, silenzio in mancanza di dati,
alias duplicati, clock del mix, sospensione di tutte le sorgenti e ripristino dopo errori.
Verificano anche lettura/scrittura JSON, volumi ripristinati, setup vuoto, dispositivi
mancanti o non apribili, file corrotto/versione errata e conservazione del file precedente.
Per l'EQ controllano risposta in dB di ogni banda, bypass, guadagno di uscita,
canali indipendenti, frequenze di campionamento diverse, transizioni, valori non
validi, invio senza REC e identità del segnale inviato e registrato. Verificano
anche persistenza EQ e compatibilità con i setup precedenti.
Per Echo verificano ritardo al campione, decadimento e singola ripetizione,
canali indipendenti, feedback massimo, bypass e cancellazione della coda,
transizioni, sample rate diversi, invio senza REC, PCM registrato identico al
segnale inviato, file originali invariati e ripristino del setup. Il test UI
verifica anche avvio, regolazioni, reset e persistenza del pannello Echo.
Per Loop verificano cattura indipendente, somma con la voce, volume, pausa e
ripetizione continua, limite di dieci clip, esportazione, pulizia, disconnessione,
salvataggio impostazioni e conversione mono/stereo con clock stabile. Il test UI
copre la terza colonna, REC/STOP/PLAY, invio simultaneo, export e chiusura senza
interrompere il loop; salva `.godot/loop-bank-preview.png` nella prova grafica.
Sono un eseguibile di test senza dipendenze da un framework di test esterno;
restituiscono codice 0 quando tutti i controlli passano.

Prova hardware: apre il microfono predefinito per mezzo secondo a volume 0,
salva e riproduce il WAV di silenzio e verifica la riapertura dell'ingresso:

```powershell
dotnet run --project tests/AudioConsole.Tests -- --hardware
```

Prova della riproduzione WASAPI reale, dopo il test deterministico: riproduce
il WAV di silenzio generato dal test e verifica avvio e fine automatica:

```powershell
dotnet run --project tests/AudioConsole.Tests -- --playback-hardware
```

Prova del loopback reale: enumera le uscite, verifica la cattura nel silenzio,
riproduce brevemente un tono a basso volume sull'uscita predefinita e controlla
che il WAV contenga il segnale. Verifica anche stereo e sospensione/ripristino:

```powershell
dotnet run --project tests/AudioConsole.Tests -- --loopback-hardware
```

Prova multisorgente reale: apre insieme microfono e uscita predefiniti, registra
un mix a volume finale 0 e verifica WAV stereo 48 kHz, clock e ripristino delle catture:

```powershell
dotnet run --project tests/AudioConsole.Tests -- --mix-hardware
```

Il test `--redirect-hardware` invia brevemente un tono a basso volume all'uscita
predefinita, sia come flusso live sia da file, e verifica il segnale tramite
loopback dello stesso endpoint, il clock e la fine naturale. Non richiede un
cavo virtuale; il collegamento verso il microfono di un'altra app si verifica
separatamente dopo aver installato il driver:

```powershell
dotnet run --project tests/AudioConsole.Tests -- --redirect-hardware
```

I test applicativi verificano inoltre invio dopo i volumi, invio contemporaneo
alla registrazione, blocco del feedback, sospensione/ripristino degli ingressi,
errori dell'uscita e ripristino del routing senza avvio automatico. Il test UI
esercita selezione destinazione/modalità, avvio/arresto e ripristino dei controlli.

Test della UI C# (dopo la build, sostituire `godot` con l'eseguibile .NET):

```powershell
godot --headless --path new-game-project res://tests/console_ui_smoke.tscn
```

Usa ingressi e uscita sintetici e verifica aggiunta/rimozione di più sorgenti,
volumi separati, duplicati, zero sorgenti e conservazione della cronologia, ascolto e catture sospese,
timer e livelli separati, selezione trascinabile e generazione del ritaglio,
salvataggio tramite DLL e conferma di uscita. Senza `--headless` verifica anche il rendering e salva
`.godot/csharp-console-preview.png`. I file temporanei sono in `.artifacts`
o `.godot`, entrambe ignorate da Git.
Il test UI usa un percorso di setup isolato e verifica salvataggio automatico,
chiusura/riapertura, volumi, configurazione vuota e file corrotto, senza toccare
il last setup reale dell'utente.

Per la verifica manuale, seleziona un ingresso, parla, registra cambiando volume,
premi Stop e riascolta dalla lista. Registra più volte senza esportare, seleziona
una voce precedente ed esportala; verifica che all'undicesima rimangano dieci
voci. Prova anche Annulla nella conferma di chiusura.
Per ritagliare, seleziona una voce, trascina un intervallo sulla timeline e premi
**Genera parte selezionata**; riascolta il ritaglio e verifica che l'originale sia ancora presente.
