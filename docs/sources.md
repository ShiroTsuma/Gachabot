# Katalog źródeł

Stan zweryfikowany 13 sierpnia 2026. Adaptery są celowo małe: transport HTTP i parser są odseparowane od diffu oraz polityki publikacji.

| Klucz | Gra | Dane | Trust | Zachowanie |
|---|---|---|---|---|
| `official-wuthering-waves` | Wuthering Waves | oficjalny `ArticleMenu.json` oraz `article/{id}.json` Kuro Games | `Official` | lista daje metadane, a osobny detal pełny tekst i obrazy |
| `official-neverness-to-everness` | NTE | paginowana lista Latest i detale Perfect World | `Official` | strony są czytane chronologicznie do pierwszego znanego wpisu |
| `game8-wuthering-waves-redeem-codes` | Wuthering Waves | Game8 active codes | `ReviewRequired` | kandydat nigdy nie publikuje się bez weryfikacji |
| `game8-neverness-to-everness-redeem-codes` | NTE | Game8 active codes | `ReviewRequired` | kandydat nigdy nie publikuje się bez weryfikacji |
| `wuwatracker-wuthering-waves-events` | Wuthering Waves | WuWa Tracker timeline | `Trusted` | wydarzenia są planowane zgodnie z ich czasem rozpoczęcia |
| `game8-neverness-to-everness-events` | NTE | Game8 event calendar | `Trusted` | wydarzenia są planowane od początku dnia serwera NTE |

## Adresy

- Wuthering Waves JSON: `https://hw-media-cdn-mingchao.kurogame.com/akiwebsite/website2.0/json/G152/en/ArticleMenu.json`
- Wuthering Waves dane artykułu: `https://hw-media-cdn-mingchao.kurogame.com/akiwebsite/website2.0/json/G152/en/article/{articleId}.json`
- Wuthering Waves publiczny artykuł: `https://wutheringwaves.kurogames.com/en/main/news/detail/{articleId}`
- NTE: `https://nte.perfectworld.com/en/article/news/index.html`
- Game8 Wuthering Waves: `https://game8.co/games/Wuthering-Waves/archives/453149`
- Game8 NTE: `https://game8.co/games/Neverness-to-Everness/archives/593718`
- WuWa Tracker timeline: `https://wuwatracker.com/pl/timeline`
- Game8 NTE events: `https://game8.co/games/Neverness-to-Everness/archives/592073`

## Polityka działania

Pierwszy poprawny skan ustawia baseline i zapisuje zawartość bez publikowania historycznych wpisów. Kolejne skany porównują tożsamość `(sourceKey, externalId)` oraz deterministyczny hash dokumentu. Zmiana tytułu lub dokumentu tworzy rewizję. Brak zmiany nie tworzy publikacji.

Surowy `ArticleMenu.json` Wuthering Waves nie jest chronologiczny: przypięte stare artykuły mogą znajdować się przed najnowszym wpisem. Handler materializuje i deduplikuje listę, parsuje `startTime`, a następnie sortuje wpisy malejąco po dacie; elementy bez poprawnej daty pozostają na końcu w kolejności źródłowej. Jest to kolejność pracy, nie granica historii. Dla WUWA `StopWhenKnown=false`, więc znany przypięty wpis zostaje pominięty, ale nie kończy skanu i nie może ukryć późniejszego nowego artykułu.

Stan całej listy jest sprawdzany zbiorczo (maksymalnie 500 identyfikatorów na zapytanie), a tylko brakujące lub wymagające naprawy wpisy pobierają detal artykułu. Koordynator zapisuje wyniki porcjami po 50. Przy pierwszym baseline nadal celowo pobierane są wszystkie nieznane detale, aby utworzyć pełne archiwum, lecz najnowsze artykuły są obsługiwane jako pierwsze. Przy kolejnym niezmienionym skanie feed WUWA wymaga tylko dwóch odczytów stanu dla obecnych 663 pozycji i nie wykonuje setek zapytań `Exists`.

Odpowiedź 403, zmiana selektorów, timeout albo niepoprawny JSON nie zatrzymują pozostałych adapterów. Błąd i czas próby trafiają do `SourceStates`, są widoczne w dashboardzie i logach. Ostatni poprawny baseline pozostaje ważny.

Brak kolejnej strony listy (`404`) oznacza naturalny koniec paginacji i nie oznacza awarii źródła. Data jest odczytywana najpierw z kafelka listy, a następnie — jako fallback — z nagłówka detalu. Dla NTE obsługiwane są formaty listy `yyyy-MM-dd` i detalu `yyyy.MM.dd`.

Źródło kodów Wuthering Waves śledzi wszystkie sekcje z kodami poza jawną listą kodów permanentnych, zamiast polegać wyłącznie na aktualnym nagłówku livestreamu. Obsługuje zarówno klasyczne wiersze z nagrodą i datą, jak i dynamiczny układ Game8: kody w tabeli, wspólny timestamp `data-date` oraz listę nagród pod tabelą. Daty wygaśnięcia są publikowane w tym samym pełnym i względnym formacie Discorda co daty eventów.

Daty eventów Wuthering Waves z WuWa Tracker są normalizowane do resetu serwera o 04:00 w skonfigurowanej strefie UTC+8. Godzina osadzona w źródłowym payloadzie nie jest używana jako godzina rozpoczęcia lub zakończenia eventu.

Definicja NTE ignoruje techniczne duplikaty obrazów z `docs.oa.wanmei.net`; artykuł potrafi zawierać obok nich właściwe grafiki z CDN `ntevmg.perfectworld.com`. Lista ignorowanych hostów jest regułą JSON (`IgnoredImageHosts`), a nie warunkiem zaszytym w parserze. Podobnie `IgnoredTitlePrefixes` pomija artykuły po początku tytułu; NTE pomija w ten sposób cykliczne „Account Action Notice”.

Game8 najpierw potrafi zwrócić HTTP `202` i dopiero po wykonaniu wyzwania JavaScript udostępnia dokument. Handler Playwright nie uznaje początkowego statusu za wynik: czeka na skonfigurowany element DOM, a deklaratywne reguły odczytują kod, nagrody i datę wygaśnięcia z wiersza tabeli. Kody bieżącej wersji i kody permanentne trafiają do osobnych wpisów (`aggregate:current` i `aggregate:permanent`). Sekcja ogólna „All Active” nie zastępuje pustej sekcji bieżącej wersji. Kod po dacie ważności jest zachowywany jako osobny rekord archiwalny z nagrodami. Nierozpoznane daty nie są publikowane. Game8 pozostaje źródłem pomocniczym i ma `ReviewRequired`.

## Definicje i handlery

Reguły znajdują się w `src/GachaBot.Web/source-definitions.json`. Resolver obsługuje obecnie:

- `json-article-feed` — lista JSON oraz opcjonalny detal JSON per identyfikator;
- `paged-html-article-feed` — lista HTML, kolejne strony, detal HTML i zatrzymanie na znanym wpisie;
- `rendered-html-code-collection` — strona dynamiczna otwierana przez Playwright; agreguje aktywne kody, nagrody i daty według selektorów z JSON.

Snapshoty prawdziwych odpowiedzi są w `test-sources/`. Testy kopiują je do katalogu wynikowego i nie łączą się z siecią. Każde odświeżenie fixture’u powinno zachować URL i datę w `test-sources/README.md`.

Diff nie jest liczony z raw HTML. Parser najpierw normalizuje stronę do bloków domenowych, a SHA-256 powstaje z ich kanonicznej reprezentacji JSON. Zmiana klas, licznika komentarzy albo innego niewyciąganego fragmentu DOM nie tworzy rewizji. Względny czas Discord używa stabilnego Unix timestampu daty wygaśnięcia, więc upływ kolejnego dnia również nie zmienia hasha.

Normalizacja treści zachowuje paragrafy, `<br>`, elementy list oraz osobne wiersze i komórki tabel. Renderer Discord escapuje składnię Markdown wyłącznie dla zwykłych bloków tekstowych; kontrolowane nagłówki, linki, pola i bloki kodu zachowują swoje formatowanie.

Testy `Game8LiveContractTests` są oznaczone jako `Explicit`. Sprawdzają rzeczywisty DOM NTE i Wuthering Waves przy użyciu dokładnej konfiguracji produkcyjnej. Instrukcja uruchomienia znajduje się w `test-sources/README.md`.

## Dodanie nowego źródła

1. Jeśli format pasuje do istniejącego handlera, dodaj definicję w `source-definitions.json`.
2. Nadaj stabilne `Key`, wybierz grę, trust, URL i reguły selektorów/pól.
3. Dodaj ten sam klucz do sekcji `Sources`, aby można go było wyłączyć lub zmienić trust.
4. Pobierz rzeczywistą odpowiedź do `test-sources/` i dodaj test kontraktowy parsera.
5. Nowy `ISourceHandler` implementuj tylko dla nowego rodzaju transportu lub nawigacji.
