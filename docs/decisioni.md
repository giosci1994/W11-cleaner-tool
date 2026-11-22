# Decisioni

Le scelte prese per il progetto, con la data. Una decisione presa non si ripropone: si cambia solo se lo chiede chi ha deciso.

| Data | Decisione | Perché |
|---|---|---|
| 18/11/2026 | Nome: **W11-cleaner-tool** | libero su GitHub (vedi `ricerca-progetti-simili.md`) |
| 18/11/2026 | Tool di pulizia e ottimizzazione per **Windows 11**, sul modello di Microsoft PC Manager: schermata semplice con pochi pulsanti | |
| 18/11/2026 | **Modalità normale (Base) facile da capire e da usare**; **modalità Esperto** per i parametri avanzati | |
| 18/11/2026 | **Repository pubblico su GitHub**: niente dati personali, token, chiavi o password nel codice, nei commit e nelle release | è pubblico |
| 18/11/2026 | Si scarica dalle **Releases di GitHub** come exe: build **x64 e ARM64**, non 32 bit | Windows 11 esiste solo a 64 bit; un exe a 32 bit verrebbe reindirizzato da `System32` a `SysWOW64` |
| 18/11/2026 | **C# (.NET), un exe solo**, licenza **MIT** | niente PowerShell compilato al momento, che fa scattare gli antivirus negli altri progetti |
| 18/11/2026 | Codice da altri progetti: solo da quelli **MIT**, con l'attribuzione. GPL e AGPL si leggono solo per le idee | licenze |
| 18/11/2026 | Funzioni: **pulizia**, **ottimizzazione** (avvio, energia), **debloat**, **checklist gaming**, **controllo XMP della RAM e dei driver**, **niente sospensione durante i download**, **log dei crash da esportare per un'IA**, **duplicati in quarantena** | dal video di Prodigeek, più i duplicati |
| 18/11/2026 | **Disiscrizione dalle newsletter: dopo**, come pezzo a parte | serve l'accesso alla posta |
| 18/11/2026 | **Annulla ogni modifica**, con lo storico | negli altri progetti i tweak rompono cose |
| 18/11/2026 | **Niente privacy e telemetria** | in winutil «Telemetry - Disable» rompe Explorer (issue #5088) |
| 18/11/2026 | **Niente installazione di app con winget** | non è pulizia |
| 18/11/2026 | **Codice SHA256** di ogni exe nella release | chi scarica può controllare che sia l'originale |
| 18/11/2026 | **L'exe lo costruisce GitHub** (GitHub Actions), non il server | scelta del proprietario; sulla macchina di sviluppo .NET non c'è |
| 18/11/2026 | **Repository privato** finché il proprietario lo prova; **pubblico solo quando lo decide lui** | scelta del proprietario |
| 18/11/2026 | Il repository sta sull'**account personale** del proprietario, **non sull'organizzazione** | scelta del proprietario |
| 18/11/2026 | Si sviluppa per **Windows 11 Home e Pro**: niente funzioni che esistono solo su Pro (Criteri di gruppo, Hyper-V); si usano registro e API presenti in entrambe | deve funzionare per chiunque |
| 18/11/2026 | Build **x64 e ARM64** entrambe a ogni rilascio, anche se il PC di prova è x64 (AMD) | conferma della scelta già presa |
| 22/11/2026 | Cambio licenza da MIT a **GPL-3.0** (codice da terzi: solo MIT o GPL-3.0 compatibili, con attribuzione; AGPL solo per le idee) | scelta del proprietario |
