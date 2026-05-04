# Lambda-RAG

> **Une plateforme déterministe, vérifiable et modulaire pour *toute revue documentaire fondée sur des règles*.**
> Un moteur. Plusieurs domaines. Mêmes données → même verdict, à chaque fois.
> Conçue pour résister à un examen juridique, réglementaire et d'audit.

> 🇬🇧 *English version: see [`README.md`](README.md).*
> 📜 *Cartographie Loi 25 : voir [`docs/regulatory/loi-25-mapping.fr.md`](docs/regulatory/loi-25-mapping.fr.md).*

Lambda-RAG transforme *toute* politique, réglementation ou modèle de
contrat en un ensemble de règles exécutables, puis projette ces règles
sur un document cible (contrat, conception d'architecture, protocole
d'entente, demande de permis, manuel d'opérations TI, etc.) et produit :

1. 📊 **Un rapport de verdict structuré** — score, par règle pass / fail / **gap** / N/A, texte de remédiation, piste d'audit complète
2. 📝 **Un document Word annoté** — modifications suivies + commentaires ancrés à la clause fautive, avec un résumé d'**ANALYSE D'ÉCARTS** en tête de document
3. 🔀 **Ou les deux** — émis depuis le même pipeline déterministe

## Pourquoi cet outil existe

Les LLM génératifs sont non déterministes. Pour la revue contractuelle, la
conformité réglementaire, l'audit ou la délivrance de permis, on ne peut
défendre un verdict qui change d'une exécution à l'autre. Lambda-RAG
applique une séparation stricte :

| Phase | Quand | LLM permis ? | Garantie de déterminisme |
|------|------|--------------|------------------------|
| **Rédaction** | Hors ligne, une fois par règle | ✅ Oui (temp = 0, validé par schéma JSON, revu par un humain) | Sortie signée, empreintée, version verrouillée |
| **Projection** | Exécution, par document | ⚠️ Code pur d'abord ; repli IA seulement en absence de projecteur, avec mise en cache complète | Mêmes octets → même projection |
| **Sélection** | Exécution, par règle × section | ❌ Jamais | Code pur — JSONPath / regex / table de sujets |
| **Évaluation** | Exécution, par section appariée | ❌ Jamais | Lambda Microsoft RulesEngine |
| **Annotation** | Exécution, par verdict | ❌ Jamais | Modifications suivies OpenXml, horodatage figé, ID épinglés |

**À l'exécution, aucun LLM n'est dans la boucle de décision.** Réexécuter
la même revue contre le même ensemble de règles produit des parties OOXML
internes octet à octet identiques dans `reviewed.docx` et un `report.json`
octet à octet identique.

> 📌 **Avant d'évaluer lambda-rag, lire
> [`docs/what-lambda-rag-is-not.md`](docs/what-lambda-rag-is-not.md).**
> C'est la fiche explicite des non-prétentions — ce que nous ne
> garantissons délibérément *pas* — et c'est la seule page la plus utile
> pour quiconque évalue si l'outil convient à un usage réglementaire.

> 🖼️ **Une image :**
> [`docs/diagrams/authoring-vs-runtime.md`](docs/diagrams/authoring-vs-runtime.md)
> est le schéma d'architecture canonique rédaction-vs-exécution. À utiliser
> dans les présentations, les articles et l'intégration.

> 📜 **Une page de prose :**
> [`docs/manifesto.md`](docs/manifesto.md) — *Projection de règles : raisonnement
> déterministe sur les documents.* Le motif, le pari et les limites
> honnêtes. À lire avant de décider si lambda-rag convient à votre problème.

## Tables de sujets sectorielles intégrées

D'emblée, Lambda-RAG livre des ontologies de sujets pour plusieurs
secteurs à forte charge de revue. Chacune fait correspondre des titres de
sections et des mots-clés en texte libre à des identifiants de sujet
canoniques sur lesquels on peut rédiger des règles :

| Table de sujets | Cas d'usage |
|-----------|-----------|
| `contract.v1` | Revue contractuelle (modalités de paiement, droit applicable, garanties, propriété intellectuelle, responsabilité, …) |
| `architecture-review.v1` | Revue d'architecture infonuagique / ASD (sécurité, réseau, conformité, performance, …) |
| `fsi.v1` | Services financiers (Bâle, LBA, KYC, suffisance du capital, risque de modèle, …) |
| `oil-gas.v1` | Politiques amont / aval (SST, intégrité des puits, intégrité des actifs, environnement, …) |
| `business-review.v1` | Protocoles d'entente, énoncés de travaux, analyses de rentabilité, revues fournisseur |
| `gov-architecture.v1` | Revue d'architecture infonuagique gouvernementale |
| `permitting.v1` | Demandes de permis et d'urbanisme |

Lister à tout moment :

```pwsh
dotnet run --project src/LambdaRag.Cli -- topic-map list
```

> 🍁 **Spécial Québec :** un ensemble de règles bilingue Loi 25 / Law 25
> est livré dans
> [`samples/contracts/loi-25-ruleset.json`](samples/contracts/loi-25-ruleset.json),
> avec sa cartographie clause par clause en
> [français](docs/regulatory/loi-25-mapping.fr.md) et en
> [anglais](docs/regulatory/quebec-law-25-mapping.md).

## 🚀 Essayer sur l'échantillon fourni

```pwsh
dotnet build
dotnet test    # tests unitaires + preuves d'idempotence

# Revue du contrat échantillon → rapport JSON
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contract.md `
  --ruleset  samples/contracts/ruleset.json `
  --out      out/sample `
  --mode     report

# Même revue → document Word annoté avec modifications suivies
# (Le mode markup nécessite un fichier .docx — utilise le contrat échantillon fourni)
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contoso-sample-contract.docx `
  --ruleset  samples/contracts/contoso-demo-ruleset.json `
  --out      out/sample `
  --mode     markup

# Ajouter des commentaires de confirmation positive ✓ pour les verdicts Pass
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contoso-sample-contract.docx `
  --ruleset  samples/contracts/contoso-demo-ruleset.json `
  --out      out/sample `
  --mode     markup `
  --annotate-pass

# Les deux à la fois
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contoso-sample-contract.docx `
  --ruleset  samples/contracts/contoso-demo-ruleset.json `
  --out      out/sample `
  --mode     both
```

Les sorties atterrissent dans `out/sample/` :

- `report.json` — verdict, score, résultat par règle, remédiation, traçabilité complète
- `reviewed.docx` — document original avec modifications suivies + commentaires + résumé d'analyse d'écarts

## 📥 Comment brancher un nouvel ensemble de règles ?

La plateforme est conçue pour qu'on puisse y déposer *n'importe quel*
ensemble de documents de politique (PDF, Word, Markdown, JSON), pour
*n'importe quel* secteur ou client, et obtenir en sortie un ensemble de
règles exécutable.

### Option A — Extraire des règles d'un dossier de documents

À privilégier lorsque l'on dispose de PDF ou Word de politiques client / régulateur.

```pwsh
# 1. Déposer les fichiers de politique dans un dossier
mkdir policies\acme-corp
# copier ACME-Procurement-Policy.pdf, ACME-DataProtection.docx, etc.

# 2. Lancer l'extracteur déterministe
dotnet run --project src/LambdaRag.Cli -- extract-rules `
  --policy-dir policies/acme-corp `
  --domain     contract `
  --id         rs_acme_procurement `
  --out        rulesets/acme-procurement.json `
  --prefix     ACME `
  --min-chars  200
```

Sortie : `rulesets/acme-procurement.json` — chaque règle inclut :

- Un énoncé en langage naturel
- Un prédicat typé (lambda) que le moteur évalue
- Un pointeur vers la portée source dans le document originel
- Une étiquette d'applicabilité (Mandatory / Conditional / Optional, déduite à la rédaction)
- Une empreinte adressée par contenu

À examiner, modifier, valider, versionner — c'est du JSON simple.

### Option B — Rédiger des règles directement (clause par clause)

Lorsqu'on a une seule clause de politique et que l'on veut une règle :

```pwsh
dotnet run --project src/LambdaRag.Cli -- author `
  --chunk  policies/acme-corp/clause-7.txt `
  --domain contract `
  --prefix ACME `
  --out    rulesets/clause-7-rule.json
```

### Option C — Rédiger un ensemble de règles à la main

Voir `samples/contracts/ruleset.json` (et l'exemple Loi 25 bilingue
[`samples/contracts/loi-25-ruleset.json`](samples/contracts/loi-25-ruleset.json)).
Le schéma est petit et documenté dans `docs/`. Tout ce qui peut s'exprimer
comme prédicat typé sur un graphe de document projeté peut devenir une règle.

### Tester l'ensemble

```pwsh
# Vérifier la couverture
dotnet run --project src/LambdaRag.Cli -- coverage `
  --document my-customer-doc.docx `
  --ruleset  rulesets/acme-procurement.json `
  --out      out/acme/coverage.json

# Lancer la revue complète
dotnet run --project src/LambdaRag.Cli -- review `
  --document my-customer-doc.docx `
  --ruleset  rulesets/acme-procurement.json `
  --out      out/acme `
  --mode     both
```

### Ajouter une nouvelle table de sujets sectorielle

Si l'ontologie nécessaire n'est pas dans la liste ci-dessus, copier
`src/LambdaRag.Projection/TopicMaps/contract.v1.json` vers
`my-industry.v1.json`, ajouter ses entêtes / alias par sujet, recompiler,
et passer `--topic-map my-industry.v1` à l'extracteur.

### Seuils numériques avec `text_features` (projecteur v1.4.0+)

Chaque section projetée porte désormais un bloc `text_features` avec des
faits numériques génériques extraits de la prose :

| Champ | Capture | Exemple |
|-------|--------|---------|
| `day_counts` / `day_count_min` / `day_count_max` | quantités de jours | `45 days`, `120-day cure`, `90 calendar days` |
| `month_counts` / `_min` / `_max` | quantités de mois | `12 months`, `36-month term` |
| `year_counts` / `_min` / `_max` | quantités d'années | `5 years`, `2-year warranty` |
| `percent_values` / `percent_min` / `percent_max` | pourcentages | `1.5%`, `30 percent` |
| `dollar_amounts` / `dollar_min` / `dollar_max` | montants en dollars | `$5,000,000`, `$1.5M`, `USD 10,000,000`, `CAD$ 2.5 million` |

Les lambdas font directement référence à ces champs — aucun code
spécifique au domaine :

```json
{
  "predicate": "input1.topics.Contains(\"insurance\") && input1.text_features.dollar_amounts.Count > 0",
  "lambda":    "input1.text_features.dollar_max >= 5000000"
}
```

Il s'agit d'un extracteur *générique* : il fonctionne sur **tout**
domaine (cautions de fournisseur, seuils de contenu recyclé ESG, fenêtres
de réponse aux permis, durées de tests sous pression de pipelines…). Le
même format de règle est utilisé pour les contrats, l'octroi de permis du
secteur public, le pétrole-et-gaz, les politiques IFI et les cadres de
gouvernance.

## Aide-mémoire CLI

```
lambda-rag review        --document <chemin> --ruleset <chemin> --out <dossier> [--mode report|markup|both] [--overlay <chemin>]
lambda-rag extract-rules --policy-dir <dossier> --domain <nom> --id <ruleset-id> --out <chemin>
lambda-rag author        --chunk <chemin> --domain <nom> --prefix <préfixe-id> --out <chemin>
lambda-rag coverage      --document <chemin> --ruleset <chemin> --out <chemin>
lambda-rag project       --document <chemin> --out <chemin>
lambda-rag parse         --document <chemin> --out <chemin>
lambda-rag index         --ruleset <chemin> [--out <chemin>]
lambda-rag topic-map     <list|show|coverage> [args]

# Gouvernance — ne modifie jamais l'ensemble de règles ; agit via diffs et surcouches
lambda-rag rules diff     <ancien.json> <nouveau.json> [--out diff.json]
lambda-rag rules show     --ruleset <chemin> --rule <id>
lambda-rag rules disable  --ruleset <chemin> --overlay <chemin> --rule <id> --reason "..." [--by <nom>]
lambda-rag rules enable   --ruleset <chemin> --overlay <chemin> --rule <id>
lambda-rag rules annotate --ruleset <chemin> --overlay <chemin> --rule <id> --note "..." [--by <nom>]
```

> Une interface Web est sur la feuille de route. Pour l'instant, tout
> s'exécute en ligne de commande et produit des fichiers que l'on peut
> diffuser, hacher, signer et expédier.

## 🛡️ Gouvernance des règles — *aucun éditeur de règles, par conception*

Lambda-RAG **ne livre délibérément pas d'éditeur de règles en place**. La
chaîne de défense juridique est :

```
PDF de politique signé  →  extract-rules  →  RuleSet.json (en git)  →  review  →  Verdict
```

Modifier une règle directement dans l'index briserait la portée source
citée, invaliderait silencieusement l'idempotence et créerait deux
sources de vérité concurrentes. La plateforme est donc opinionnée :

> **Le document de politique fait foi. Le RuleSet est sa forme compilée.
> Les deux sont versionnés. Aucun n'est édité en production.**

Lorsqu'une règle doit légitimement changer, on modifie le document de
politique et on relance `extract-rules`. Pour voir ce qui a changé :

```pwsh
lambda-rag rules diff old-ruleset.json new-ruleset.json --out delta.json
```

On obtient les règles ajoutées / retirées / modifiées et, pour chaque
règle *modifiée*, la liste exacte des champs qui ont dérivé (`predicate`,
`lambda`, `severity`, `applicability`, `schema`, `naturalLanguage`,
`version`). Le code de sortie est `2` lorsqu'il y a des deltas — à câbler
en CI pour gater la promotion des ensembles.

### Quand on doit légitimement « éditer une règle » sans réextraire

Il existe exactement deux cas, traités via une **RuleOverlay** annexe —
*jamais* en mutant l'ensemble :

1. **Désactiver une règle** — p. ex. « la règle X est supplantée par une lettre d'accompagnement »

   ```pwsh
   lambda-rag rules disable `
     --ruleset rulesets/acme.json `
     --overlay rulesets/acme.overlay.json `
     --rule    ACME-PAY-003 `
     --reason  "supplantée par la clause 4.2 de la lettre 2026-T2" `
     --by      legal@acme.com
   ```

2. **Annoter une règle** — commentaire de réviseur qui ne change *pas* le verdict

   ```pwsh
   lambda-rag rules annotate `
     --ruleset rulesets/acme.json `
     --overlay rulesets/acme.overlay.json `
     --rule    ACME-LIAB-001 `
     --note    "voir clause 7.2 du MSA — plafonné aux frais payés au cours des 12 derniers mois" `
     --by      legal@acme.com
   ```

Puis lancer une revue avec la surcouche appliquée :

```pwsh
lambda-rag review `
  --document customer-doc.docx `
  --ruleset  rulesets/acme.json `
  --overlay  rulesets/acme.overlay.json `
  --out      out/customer
```

Propriétés des surcouches qui les rendent **sûres** :

- 🔒 **Liées à un ID + version d'ensemble précis** — refuse de s'appliquer à un autre ensemble
- 🧾 **Chaque désactivation porte une `reason` et un horodatage `at`** (et facultativement `by`) — `--reason` est obligatoire
- 🔍 **Consignée dans le rapport** — `report.json` comporte un bloc `overlayApplied` avec l'empreinte SHA-256 de la surcouche, la liste désactivée et les annotations
- 📁 **JSON annexe, pas une base de données** — à stocker à côté de l'ensemble dans git ; revue par PR ; retour arrière via `rules enable`
- ➖ **Ne modifie jamais le `predicate`, `lambda`, `severity` ou `applicability`** — ces changements doivent passer par le pipeline politique → extract

C'est le motif utilisé par la gestion de versions de binaires signés,
appliqué aux règles. On obtient toute la valeur pratique d'un « éditeur »
(désactiver, annoter) sans le risque de chaîne de garde.

## Disposition de la solution

```
src/
  LambdaRag.Core/         Domaine, hachage, sélecteurs, abstractions
  LambdaRag.Parsing/      Parseurs PDF/DOCX/MD → ParsedDocument
  LambdaRag.Projection/   ParsedDocument → ProjectedDocument + tables de sujets
  LambdaRag.Selectors/    Concordance JSONPath (sous-ensemble)
  LambdaRag.Evaluation/   Wrapper Microsoft RulesEngine, agrégateur de verdicts
  LambdaRag.Markup/       Annotateur OpenXml en modifications suivies (déterministe)
  LambdaRag.Authoring/    Agents MAF : extraire les règles depuis les documents
  LambdaRag.Persistence/  Stockages SQLite : règles, projections, évaluations
  LambdaRag.Api/          API minimale ASP.NET Core (à venir)
  LambdaRag.Cli/          Outil en ligne de commande `lambda-rag`
tests/
  LambdaRag.UnitTests/             tests unitaires
  LambdaRag.IdempotencyTests/      preuves d'égalité octet-à-octet
samples/contracts/                 contract.md + ruleset.json + loi-25-ruleset.json
docs/                              ARCHITECTURE.md, DETERMINISM.md, SELECTORS.md, regulatory/*
```

## Feuille de route

> **Phase 0 (clôture de crédibilité) — ✅ complète.** Analyse d'écarts Contoso,
> idempotence par étalon-or de `reviewed.docx`, cadrage défendable de la
> précision, [`what-lambda-rag-is-not.md`](docs/what-lambda-rag-is-not.md)
> et un [plan de contingence Roslyn-scripting](docs/dependencies/rules-engine-risk.md)
> pour la dépendance RulesEngine sont tous livrés. Voir
> [`CHANGELOG.md`](CHANGELOG.md) et le
> [filtre des problèmes phase-0](https://github.com/MTCMarkFranco/lambda-rag/issues?q=is%3Aissue+label%3Aphase-0-credibility).

> **P1.4 (cartographie Loi 25 du Québec) — ✅ livré.** Cartographie
> bilingue clause par clause de la Loi 25 / Law 25
> (P-39.1 + A-2.1) avec 25 règles `QC-LOI25-*` dans
> [`samples/contracts/loi-25-ruleset.json`](samples/contracts/loi-25-ruleset.json),
> les documents de cartographie en [français](docs/regulatory/loi-25-mapping.fr.md) et en
> [anglais](docs/regulatory/quebec-law-25-mapping.md), et le présent README.fr.md.

Les phases 1–5 (motif canonique, coins réglementaires canadiens,
distribution, gouvernance + outillage, écosystème) vivent comme des
issues GitHub étiquetées. Court terme :

- 🖥️ Interface Web légère (glisser-déposer document + ensemble → verdict + .docx annoté)
- 🔌 Volet Word en direct pour revue en place (actuellement seulement annotation .docx hors ligne)
- 🌐 Surface d'API REST dans `LambdaRag.Api` exposant le même pipeline
- ✅ Commentaires de confirmation positive en mode annotation (actuellement seuls Fail / Gap / Error remontent)

## Licence

MIT.
