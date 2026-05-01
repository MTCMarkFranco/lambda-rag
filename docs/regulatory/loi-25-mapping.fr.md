# Loi 25 du Québec — Cartographie Lambda-RAG (FR)

> **Statut :** Phase 1 / [P1.4](https://github.com/MTCMarkFranco/lambda-rag/issues/14).
> La Loi 25 est **en vigueur** au Québec — contrairement au projet de loi
> fédéral C-27 / LIAD. Ce document est donc une cartographie du **droit en
> vigueur**, et non prospective. La revue par des experts en la matière (SME)
> est *en attente* (voir [§ Réviseurs SME](#recommandations-de-réviseurs-sme-engagement-en-attente)).
>
> **Sources (publiques, sans accès payant) :** [LégisQuébec — *Loi sur la
> protection des renseignements personnels dans le secteur privé* (P-39.1)](https://www.legisquebec.gouv.qc.ca/fr/document/lc/p-39.1)
> · [LégisQuébec — *Loi sur l'accès aux documents des organismes publics et
> sur la protection des renseignements personnels* (A-2.1)](https://www.legisquebec.gouv.qc.ca/fr/document/lc/a-2.1)
> · [Commission d'accès à l'information du Québec (CAI)](https://www.cai.gouv.qc.ca/) —
> guides, bulletins, décisions · [Loi 25 (2021, c.25)](https://www.publicationsduquebec.gouv.qc.ca/fileadmin/Fichiers_client/lois_et_reglements/LoisAnnuelles/fr/2021/2021C25F.PDF) (loi modificatrice).
> Les extraits cités sont reproduits aux fins d'interprétation réglementaire
> au titre de l'utilisation équitable. Le texte primaire est consolidé au
> **2026-03-31** via la chaîne de consolidation publique `legisquebec.gouv.qc.ca`.
>
> **Public visé :** organisations assujetties à la Loi 25 (toute *personne qui
> exploite une entreprise* au Québec, ainsi que les *organismes publics* visés
> par la Loi A-2.1) ; lecteurs comparant la Loi 25 à la LPRPDE / projet C-27 /
> RGPD.
>
> **Documents compagnons :** [`quebec-law-25-mapping.md`](quebec-law-25-mapping.md)
> (miroir anglais, texte EN au premier plan) ; [`bill-c27-aida-mapping.md`](bill-c27-aida-mapping.md) ;
> [`osfi-e23-mapping.md`](osfi-e23-mapping.md) ; [`tbs-adm-mapping.md`](tbs-adm-mapping.md).
>
> **Ensemble de règles :** [`samples/contracts/loi-25-ruleset.json`](../../samples/contracts/loi-25-ruleset.json) — 25 règles `QC-LOI25-*` rédigées à la main.

---

## Pourquoi cette cartographie compte

La Loi 25 est la loi canadienne sur la protection des renseignements
personnels la plus stricte actuellement en vigueur. Pour les institutions
financières du Québec (Desjardins, Banque Nationale, iA Groupe financier,
SSQ), les organismes publics (ministères, RAMQ, Hydro-Québec, municipalités)
et toute entreprise traitant des renseignements personnels de résidents du
Québec, les obligations de la Loi 25 façonnent désormais les clauses
contractuelles, la diligence raisonnable des fournisseurs et la conception
des produits. Plusieurs obligations — confidentialité par défaut (art. 9.1),
révision humaine d'une décision automatisée (art. 12.1), ÉFVP avant transfert
hors Québec (art. 17), sanctions administratives pécuniaires pouvant
atteindre **10 M\$ ou 2 % du chiffre d'affaires mondial** (sanction pénale
jusqu'à **25 M\$ ou 4 %**) — sont plus strictes que la LPRPDE et égalent ou
dépassent la LPVPC / le RGPD.

Un ensemble de règles bilingue dans lambda-rag offre aux organisations
québécoises (a) un rapport d'écarts déterministe sur les contrats / EATP /
politiques de confidentialité, et (b) une expérience de revue *en français
d'abord* que les outils unilingues anglais ne peuvent reproduire.

## Comment lire ce document

Chaque rangée associe un article de la Loi 25 à une règle `QC-LOI25-*`
livrée dans [`samples/contracts/loi-25-ruleset.json`](../../samples/contracts/loi-25-ruleset.json) :

| Champ | Signification |
|---|---|
| **Article** | Référence législative — `P-39.1 art. X` (secteur privé) ou `A-2.1 art. X` (secteur public) |
| **Obligation** | Ce que l'entité réglementée doit démontrer |
| **ID de règle** | Identifiant `QC-LOI25-*` stable dans le fichier JSON |
| **Sévérité** | `Critical` (violation manifeste ; risque de SAP) / `Violation` (non-conformité avérée) / `Deviation` (artefact opérationnel manquant) / `Suggestion` (renforcement) |
| **Réviseur** | `qc-privacy` (secteur privé) ou `qc-public-sector` (spécifique à A-2.1) |

Le format-fil est identique à [`contoso-demo-ruleset.json`](../../samples/contracts/contoso-demo-ruleset.json).
Chaque règle porte une formulation française dans `metadata.naturalLanguageFr`
(les métadonnées sont opaques pour le moteur — aucun changement de code n'a
été nécessaire).

---

## Définitions / ancres terminologiques (FR + EN)

Ces termes orientent les filtres de prédicat ; ils ne deviennent pas des règles
autonomes.

| Français (canonique) | Anglais | Ancrage statutaire |
|---|---|---|
| **Renseignement personnel** | Personal information — toute information qui, *seule ou en combinaison avec d'autres*, permet d'identifier une personne physique | P-39.1 art. 2 ; A-2.1 art. 54 |
| **Renseignement personnel sensible** | Sensitive personal information — renseignement dont l'usage révèle des éléments intimes / médicaux / financiers / biométriques suscitant une attente accrue de vie privée | P-39.1 art. 12 ¶3 |
| **Responsable de la protection des renseignements personnels (RPRP)** | Person in charge of the protection of personal information / Privacy Officer / DPO | P-39.1 art. 3.1 ; A-2.1 art. 8 |
| **Évaluation des facteurs relatifs à la vie privée (ÉFVP)** | Privacy Impact Assessment (PIA) | P-39.1 art. 3.3 ; A-2.1 art. 63.5 |
| **Décision fondée exclusivement sur un traitement automatisé** | Decision based exclusively on automated processing | P-39.1 art. 12.1 |
| **Profilage** | Profiling — collecte et utilisation de RP afin d'évaluer les caractéristiques d'une personne physique (rendement au travail, situation économique, santé, préférences, comportement) | P-39.1 art. 8.1 ¶2 |
| **Incident de confidentialité** | Confidentiality incident — accès, utilisation, communication ou perte de RP non autorisé par la loi, *ou* toute autre atteinte à la protection des RP | P-39.1 art. 3.6 ; A-2.1 art. 63.8 |
| **Consentement manifeste, libre, éclairé, donné à des fins spécifiques** | Manifest, free, informed consent given for specific purposes | P-39.1 art. 14 |
| **Désindexation / Cessation de la diffusion** | De-indexing / cessation of dissemination | P-39.1 art. 28.1 |
| **Portabilité** | Portability — droit d'obtenir les RP informatisés dans un format structuré et couramment utilisé | P-39.1 art. 27 |
| **Anonymisation** | Anonymization — empêcher de façon irréversible l'identification selon les pratiques généralement reconnues et les critères prévus par règlement (distinct de la dépersonnalisation) | P-39.1 art. 23 ¶2 |
| **Commission d'accès à l'information (CAI)** | L'autorité de surveillance et le tribunal administratif québécois en matière de vie privée | Toute la loi |
| **Sanction administrative pécuniaire (SAP)** | Administrative monetary penalty (AMP) | P-39.1 art. 90.1–90.13 |

> 📌 **Modèle de prédicat :** pour une règle propre au Québec, conditionner
> sur `input1.topics.Contains("privacy")` plus un filtre Québec / Loi 25 /
> mots-clés français, p. ex.
> `input1.text.Contains("Quebec") || input1.text.Contains("Québec") || input1.text.Contains("Loi 25") || input1.text.Contains("P-39.1")`.
> Aucune modification du moteur n'est requise.

---

## Calendrier d'entrée en vigueur

La Loi 25 a été sanctionnée le **2021-09-22**, mais ses dispositions sont
entrées en vigueur en trois tranches successives :

| Date | Tranche | Points saillants |
|---|---|---|
| **2022-09-22** | Phase 1 | Désignation du RPRP (art. 3.1) ; gestion et registre des incidents de confidentialité, notification à la CAI et aux personnes concernées (art. 3.5–3.8 ; A-2.1 art. 63.8–63.11). |
| **2023-09-22** | Phase 2 | Cadre de gouvernance + publication (art. 3.2) ; ÉFVP pour tout nouveau projet TI (art. 3.3) ; évaluation des transferts hors Québec (art. 17) ; avis de profilage (art. 8.1) ; publication de la politique de confidentialité (art. 8.2) ; confidentialité par défaut (art. 9.1) ; consentement granulaire (art. 14) ; divulgation des décisions automatisées + révision humaine (art. 12.1) ; divulgation à la CAI ≥ 60 jours pour les banques biométriques (LCCJTI art. 45). |
| **2024-09-22** | Phase 3 | Droit à la portabilité (art. 27) ; droit à la cessation de la diffusion / désindexation (art. 28.1) ; activation complète du régime de SAP. |

Les pouvoirs d'application et les SAP de la CAI sont pleinement actifs depuis
le **2024-09-22**.

---

## Tableau Article → Règle

### Loi du secteur privé (P-39.1)

| Article | Obligation | ID de règle | Sévérité |
|---|---|---|---|
| **art. 3.1** | Désigner un RPRP et publier son titre et ses coordonnées | `QC-LOI25-DPO-001` | Critical |
| **art. 3.1 ¶2** | Délégation écrite si le RPRP n'est pas le plus haut dirigeant | `QC-LOI25-DPO-002` | Deviation |
| **art. 3.2** | Établir + publier un cadre de gouvernance des RP (rôles, conservation, plaintes, formation) | `QC-LOI25-GOV-001` | Violation |
| **art. 3.3** | Réaliser une ÉFVP pour les nouveaux projets TI impliquant des RP | `QC-LOI25-PIA-001` | Violation |
| **art. 3.5–3.7** | Procédure écrite de gestion des incidents (évaluation du risque, atténuation, notification CAI + personnes en cas de risque de préjudice sérieux) | `QC-LOI25-INC-PROC-001` | Critical |
| **art. 3.8** | Tenir un registre des incidents de confidentialité ; copie à la CAI sur demande | `QC-LOI25-INC-REG-001` | Violation |
| **art. 8 ¶2** | Informer la personne concernée de la possibilité de communication des RP hors Québec | `QC-LOI25-XBORDER-NOTICE-001` | Violation |
| **art. 8.1** | Avis + mécanisme de désactivation pour toute technologie d'identification, localisation ou profilage | `QC-LOI25-PROFILE-001` | Critical |
| **art. 8.2** | Publier une politique de confidentialité en termes simples et clairs sur le site Web public | `QC-LOI25-POLICY-PUB-001` | Violation |
| **art. 9.1** | Confidentialité par défaut (paramètres les plus protecteurs, sans intervention de l'utilisateur) | `QC-LOI25-DEFAULT-001` | Violation |
| **art. 11** | Conserver ≥ 1 an après la décision les RP utilisés pour décider à l'égard d'un employé | `QC-LOI25-HR-RETENTION-001` | Deviation |
| **art. 12.1 ¶1** | Informer la personne concernée d'une décision exclusivement automatisée au plus tard au moment de la décision | `QC-LOI25-AUTODEC-001` | Critical |
| **art. 12.1 ¶2** | Sur demande : divulguer les RP utilisés + principaux facteurs et paramètres + droit de rectification | `QC-LOI25-AUTODEC-002` | Critical |
| **art. 12.1 ¶3** | Donner l'occasion de présenter des observations à un membre du personnel pouvant réviser la décision | `QC-LOI25-AUTODEC-003` | Critical |
| **art. 14** | Consentement manifeste, libre, éclairé, par finalité, présenté distinctement | `QC-LOI25-CONSENT-001` | Critical |
| **art. 14 ¶3** | Consentement parental / du tuteur pour les mineurs de moins de 14 ans | `QC-LOI25-CONSENT-MINOR-001` | Critical |
| **art. 17** | ÉFVP + entente écrite avant tout transfert de RP hors Québec | `QC-LOI25-XBORDER-001` | Critical |
| **art. 18 ¶ dérogations** | Tenir un registre des communications de RP effectuées sans consentement en vertu d'exceptions | `QC-LOI25-DISCLOSE-LOG-001` | Deviation |
| **art. 23** | Détruire ou anonymiser les RP une fois la finalité accomplie | `QC-LOI25-RETENTION-001` | Violation |
| **art. 27** | Sur demande, communiquer les RP informatisés dans un format structuré et couramment utilisé (portabilité) | `QC-LOI25-PORTABILITY-001` | Violation |
| **art. 28.1** | Procédure pour traiter les demandes de cessation de diffusion, désindexation ou réindexation | `QC-LOI25-DEINDEX-001` | Violation |

### Loi du secteur public (A-2.1)

| Article | Obligation | ID de règle | Sévérité |
|---|---|---|---|
| **art. 8.1** | Constituer un comité sur l'accès à l'information et la protection des RP | `QC-LOI25-PUB-COMMITTEE-001` | Violation |
| **art. 67.3–67.4** | Tenir un registre des communications de RP en vertu des art. 67.1 / 67.2.1 et y donner accès public | `QC-LOI25-PUB-COMMS-REG-001` | Violation |

### Renvoi LCCJTI (biométrie)

| Article | Obligation | ID de règle | Sévérité |
|---|---|---|---|
| **LCCJTI art. 45** | Divulguer toute banque biométrique à la CAI ≥ 60 jours avant la mise en service | `QC-LOI25-BIOMETRIC-CAI-001` | Critical |

### Renvois dans la couche fournisseurs / EATP

| Déclencheur | Obligation | ID de règle | Sévérité |
|---|---|---|---|
| **art. 3.5, 3.6, 17 (privé) ; art. 67.2 (public)** | Les ententes fournisseurs / EATP imposent des obligations équivalentes à la Loi 25 + notification rapide des incidents | `QC-LOI25-VENDOR-DPA-001` | Violation |

---

## Exemple détaillé — `QC-LOI25-AUTODEC-002`

```json
{
  "id": "QC-LOI25-AUTODEC-002",
  "version": "1.0.0",
  "naturalLanguage": "On request, disclose the personal information used, the principal factors and parameters that led to the decision, and the data subject's right to have inaccurate input data corrected.",
  "predicate": "(input1.topics.Contains(\"ai\") || input1.topics.Contains(\"privacy\")) && (input1.text.Contains(\"automat\") || input1.text.Contains(\"algorithm\") || input1.text.Contains(\"algorithme\"))",
  "lambda": "(input1.text.Contains(\"factors\") || input1.text.Contains(\"facteurs\") || input1.text.Contains(\"parameters\") || input1.text.Contains(\"paramètres\") || input1.text.Contains(\"principal\") || input1.text.Contains(\"principaux\")) && (input1.text.Contains(\"correct\") || input1.text.Contains(\"rectif\") || input1.text.Contains(\"update\") || input1.text.Contains(\"mettre à jour\"))",
  "severity": "Critical",
  "evidenceQuote": "Elle doit aussi, à la demande de la personne concernée, l'informer : 1° des renseignements personnels utilisés pour rendre la décision ; 2° des raisons et des principaux facteurs et paramètres ayant mené à la décision ; 3° du droit de la personne concernée de faire rectifier les renseignements personnels utilisés pour rendre la décision.",
  "metadata": {
    "reviewer": "qc-privacy",
    "lawReference": "P-39.1 art. 12.1 ¶2",
    "naturalLanguageFr": "À la demande, divulguer les renseignements personnels utilisés, les principaux facteurs et paramètres de la décision et le droit de rectification."
  }
}
```

---

## Tableau comparatif — Loi 25 vs LPRPDE / LPVPC-LIAD / RGPD / Loi A-2.1

| Thème | Loi 25 (P-39.1) | LPRPDE | LPVPC / LIAD (projet C-27) | RGPD | Loi A-2.1 (public) |
|---|---|---|---|---|---|
| Responsable / DPO | **Obligatoire + coordonnées publiées** (art. 3.1) | Principe de responsabilité ; pas nommément | Requis (LPVPC) | DPO requis selon les seuils Art. 37 | Obligatoire ; titre communiqué à la CAI |
| Cadre de gouvernance | **Obligatoire + publié** (art. 3.2) | Implicite | Requis (LPVPC) | Art. 24/30 — registres de traitement | Requis, encadré par règlement |
| ÉFVP / DPIA | **Obligatoire** pour nouveaux projets TI + transferts hors Québec (art. 3.3, 17) | Non obligatoire | Risque élevé seulement (LPVPC) ; à fort impact en IA (LIAD) | DPIA selon Art. 35 | Obligatoire + comité consulté (art. 63.5) |
| Confidentialité par défaut | **Explicite** (art. 9.1) | Non explicite | Non explicite | Art. 25 (plus large, moins précis) | Miroir art. 63.7 |
| Consentement | **Manifeste, libre, éclairé, par finalité, présenté distinctement** (art. 14) | Connaissance + consentement | Exceptions « activités d'affaires » élargies (LPVPC) | Art. 6/7 — consentement ou autres bases | Souvent autorisation légale ; consentement résiduel |
| Mineurs | **< 14 ans : consentement parental / tuteur** | Neutre quant à l'âge | Sensibilité étendue à tous les mineurs | Art. 8 — âge fixé par État membre (13–16) | Même seuil de 14 ans |
| Décisions automatisées | **Avis au moment de la décision + facteurs + révision humaine** (art. 12.1) | Aucun | Droit à révision humaine (LPVPC) | Art. 22 — droit de ne pas être soumis + garanties | Aucun analogue direct |
| Transferts hors Québec | **ÉFVP + entente écrite** (art. 17) | Responsabilité par contrat de protection comparable | Non explicite | Chapitre V (CCT / adéquation) | Miroir art. 70.1 |
| Notification d'incident | CAI + personnes en cas de **risque de préjudice sérieux** (art. 3.5) | Risque réel de préjudice grave (LPRPDE art. 10.1) | Idem | Art. 33/34 (compteur de 72 h) | Miroir art. 63.8–63.10 |
| Registre des incidents | **Obligatoire ; copie à la CAI sur demande** (art. 3.8) | Non spécifié | Non spécifié | Art. 33 ¶5 — registres internes | Miroir art. 63.11 |
| Portabilité | **Obligatoire** (art. 27 — depuis 2024-09-22) | Absente | Présente (LPVPC) | Art. 20 | Miroir via modifications de la A-2.1 |
| Désindexation / cessation | **Obligatoire** (art. 28.1 — depuis 2024-09-22) | Absente | Absente | Art. 17 (effacement plus large) | Miroir dans la A-2.1 |
| SAP (max) | **10 M\$ ou 2 % du CA mondial** ; pénal **25 M\$ ou 4 %** | s/o (la CFC peut émettre des ordonnances) | LPVPC : 3 % CA mondial (SAP) / 5 % (pénal) | 4 % CA mondial | Idem P-39.1 |
| Spécifique biométrie | **Divulgation préalable à la CAI ≥ 60 jours** (LCCJTI art. 45) | Aucun | Aucun | Art. 9 (catégorie spéciale) | Idem |

---

## Cartographie sévérité → SAP

Le champ `severity` de l'ensemble de règles est ancré dans le cadre
d'application de la CAI :

| Sévérité | À utiliser quand… | Exemple de règle |
|---|---|---|
| **Critical** | Violation manifeste susceptible d'entraîner une SAP ou une sanction pénale (RPRP absent, consentement absent, profilage sans avis, décision automatisée sans divulgation, transfert hors Québec sans ÉFVP) | `QC-LOI25-DPO-001`, `QC-LOI25-CONSENT-001`, `QC-LOI25-AUTODEC-001`, `QC-LOI25-XBORDER-001` |
| **Violation** | Divulgation / publication requise manquante | `QC-LOI25-POLICY-PUB-001`, `QC-LOI25-PORTABILITY-001`, `QC-LOI25-DEINDEX-001` |
| **Deviation** | Artefact opérationnel manquant (registre, délégation écrite, calendrier de conservation) | `QC-LOI25-INC-REG-001`, `QC-LOI25-DPO-002`, `QC-LOI25-DISCLOSE-LOG-001` |
| **Suggestion** | Renforcement recommandé au-delà du minimum (aucun en v1.0.0 ; réservé pour les prochaines modifications) | — |

---

## Pointeur vers le JSON

Lancer une revue à l'aide de l'ensemble de règles fourni :

```pwsh
dotnet run --project src/LambdaRag.Cli -- review `
  --document chemin/vers/votre-contrat-ou-politique.docx `
  --ruleset  samples/contracts/loi-25-ruleset.json `
  --out      out/loi-25 `
  --mode     both
```

Le `report.json` cite l'ID de règle, la section concordante, la formulation
française + anglaise, la référence législative et la remédiation. Le
`reviewed.docx` contient les modifications suivies et les commentaires
ancrés aux clauses problématiques.

---

## Ambiguïtés et questions ouvertes

Le pack du Researcher a relevé plusieurs zones où la doctrine sous Loi 25
n'est pas encore pleinement stabilisée :

1. **Profondeur de l'ÉFVP transfrontalière.** L'article 17 P-39.1 exige
   l'évaluation du régime juridique étranger, mais la CAI n'a pas publié de
   modèle d'ÉFVP ni de liste d'adéquation par pays. La pratique converge en
   IFI québécoise vers un questionnaire écrit + annexe de type CCT, mais
   on doit s'attendre à des variations jusqu'à ce que la CAI publie un guide
   formel.
2. **Calibrage des SAP.** Les pouvoirs SAP de la phase 3 sont actifs depuis
   le 2024-09-22. À la consolidation 2026-03-31, seules quelques décisions
   SAP publiques existent (la plus élevée d'environ 7 000 \$ CAD plus une
   amende pénale de 15 000 \$ CAD). La distribution à grande échelle n'est
   pas encore observable ; les sévérités du présent ensemble seront
   réévaluées une fois publiée une synthèse pluriannuelle de la CAI.
3. **Portée du « profilage ».** L'art. 8.1 vise l'identification, la
   localisation et le profilage. Les bulletins et webinaires CAI 2023
   traitent les témoins (cookies) et l'analytique web comme étant dans le
   périmètre ; la pratique IFI a convergé vers la mise à jour des bannières
   de témoins. La frontière entre « analytique » et « profilage » n'est pas
   encore parfaitement stable.
4. **Seuil d'anonymisation.** L'art. 23 ¶2 exige une non-identification
   irréversible selon « les *pratiques généralement reconnues et les critères
   prévus par règlement* ». Le règlement d'application est entré en vigueur
   en 2024 mais laisse plusieurs seuils sectoriels ouverts. La pratique
   prudente consiste à traiter les données prétendument anonymisées comme
   encore personnelles tant qu'une attestation tierce formelle n'est pas en
   place.
5. **Critères de désindexation.** Le test d'équilibre d'intérêt public de
   l'art. 28.1 (inexactitude / obsolescence / atteinte sérieuse vs intérêt
   public continu) est nouvellement actif (2024-09-22). On peut s'attendre
   à des éclaircissements importants de la CAI et à une jurisprudence sur
   2025–2027.
6. **Articulation avec la LPVPC fédérale.** Si la LPVPC est adoptée,
   plusieurs dispositions de la Loi 25 (consentement, notification
   d'incident) seront substantiellement similaires, mais le Québec conservera
   sa primauté dans le cadre de la procédure d'exemption « substantiellement
   similaire ». Les flux de données Québec → reste du Canada pourraient
   nécessiter une analyse à double régime tant que le régime fédéral n'est
   pas stabilisé.

---

## Recommandations de réviseurs SME *(engagement en attente)*

Le pack du Researcher a identifié plusieurs experts du domaine public dont
les travaux publiés couvrent la Loi 25 en profondeur. Il s'agit
**uniquement de suggestions — aucun n'a été engagé au moment de la
rédaction**. L'engagement est suivi dans
[issue #14 follow-ups](https://github.com/MTCMarkFranco/lambda-rag/issues/14).

| Réviseur | Affiliation (rôle public) | Pourquoi |
|---|---|---|
| **Me Antoine Aylwin** | Associé, Fasken — pratique en vie privée et accès à l'information | Commentaires publics réguliers sur la Loi 25, participation à des groupes de travail CAI |
| **Me Charles Morgan** | Associé, McCarthy Tétrault — coprésident du Groupe national Cybersécurité/Données | Notes pratiques publiées sur l'ÉFVP transfrontalière en IFI québécoise |
| **Me Patrick Cormier** | DPO Canada / communauté de formation en vie privée | Maintient des programmes de mise à niveau Loi 25 largement cités, en FR + EN |
| **Pr. Karim Benyekhlef** | Université de Montréal, CRDP — directeur, Laboratoire de cyberjustice | Profondeur académique sur la décision automatisée et le privacy-by-design |
| **Mme Diane Poitras** | *(ancienne)* présidente de la CAI | Perspective de régulateur sur le cadre d'application (post-mandat ; consulter uniquement les déclarations publiées) |

> 📌 **Protocole d'engagement :** avant qu'une attribution de réviseur
> n'apparaisse dans une version publiée du présent document, obtenir une
> confirmation écrite. D'ici là, les noms ci-dessus sont des **balises
> publiques**, et non des citations.

---

## Couverture des tests

L'ensemble Loi 25 est exercé par :

- **`QuebecLaw25RulesetParserTests`** — analyse `loi-25-ruleset.json`,
  affirme la validité du schéma, un nombre de règles ≥ 20 et la présence,
  pour chaque règle, de `metadata.naturalLanguageFr`, `metadata.lawReference`
  et d'un `evidenceQuote` non vide.
- **`GenericQuebecRuleEvaluationTests`** — synthétise un document non
  québécois (EATP générique) et un document pertinent au Québec, exécute
  l'ensemble QC-LOI25 sur les deux et affirme (a) que le moteur produit des
  verdicts de manière identique à tout autre ensemble (aucun chemin de code
  spécifique au Québec) et (b) que les règles conditionnées par mots-clés
  Québec ne se déclenchent que sur le document pertinent.

Ces deux tests constituent la **garantie de généricité** : tout changement
futur qui figerait un comportement spécifique au Québec dans `src/` les
fera échouer.

---

## Attribution des sources et avis de statut juridique

LégisQuébec, *Loi sur la protection des renseignements personnels dans le
secteur privé* (P-39.1) et *Loi sur l'accès aux documents des organismes
publics et sur la protection des renseignements personnels* (A-2.1), telles
que modifiées par la *Loi modernisant des dispositions législatives en
matière de protection des renseignements personnels* (Loi 25, 2021 c.25).
© Gouvernement du Québec. Les extraits cités sont reproduits au titre de
l'utilisation équitable.

**Statut juridique au moment de la rédaction :** la Loi 25 est pleinement
**en vigueur**. La cartographie reflète la consolidation LégisQuébec au
**2026-03-31**. Les lecteurs devraient revalider sur la consolidation
LégisQuébec courante avant de s'appuyer sur le présent document pour des
décisions de conformité.

Le présent document **ne constitue pas un avis juridique**. Il s'agit d'une
cartographie structurelle destinée à amorcer un ensemble de règles
déterministe ; consulter un avocat québécois qualifié pour toute
interprétation contraignante.
