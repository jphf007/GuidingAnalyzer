# GuidingAnalyzer 1.0.0.5 — audit avant publication

Audit local du 10 novembre 2026. Candidat non publié. Les archives 1.0.0.4 déjà distribuées restent conservées.

## Périmètre et invariants

Le fichier de reconstruction `Services/MountCurveService.cs` est inchangé, octet pour octet, par rapport au début de l'audit. RA reste identique à 1.0.0.2, DEC conserve la correction inverse et le raccordement après dithering de 1.0.0.4. Aucune suppression de dérive DEC n'est ajoutée. Le premier graphique conserve les mesures du log ; seules les lignes malformées ne sont plus considérées comme des mesures valides à zéro.

L'identifiant permanent du plugin reste `A1B2C3D4-E5F6-7890-ABCD-EF1234567890`, vérifié contre le manifeste 1.0.0.4 actuellement accepté dans le catalogue N.I.N.A. La version minimale reste 3.2.0.9001 et la cible .NET 8 Windows/WPF.

## Défauts reproduits et corrigés

- Lecture du log : une calibration échouée ou un en-tête tronqué pouvait absorber la session suivante ; les lignes absentes, NaN ou invalides ne doivent pas devenir des mesures valides à zéro. Les dates de début utilisent un format invariant.
- Calibration : les angles négatifs, présents dans le log utilisateur (-179.5° et -88.2°), étaient affichés à zéro. Les angles sont maintenant lus avec leur signe, dans la calibration et dans l'en-tête de session.
- Statistiques : exclusion des dithers et mesures non finies au niveau du service ; cohérence entre la note et son descriptif ; même segment continu pour la dérive calculée et affichée.
- Backlash : un pulse suivi de zéro n'est plus compté comme une inversion. Le diagnostic exige une inversion entre pulses non nuls dans un segment valide. Les états `enabled`, `true` et `1` sont tous reconnus. Les résidus restent des indices à confirmer, pas une mesure de backlash ou de délai.
- Surcorrection : les changements de signe d'un signal sans pulses ne déclenchent plus un diagnostic de guidage agressif. Corrélation et dérive ne sont plus présentées comme une preuve de flexion différentielle.
- Période mécanique : seuls les pics retenus par le filtrage spectral alimentent les candidats de période et la liste des composantes. Du bruit seul ne produit plus artificiellement une période de vis et cinq bins voisins présentés comme harmoniques.
- Altitude/RMS : latitude estimable dans les deux hémisphères ; coordonnées absentes rejetées. Les fenêtres restent dans un segment valide d'une même session, sans traverser perte d'étoile, dither, interruption ou changement de session. Le RMS est une racine de moyenne des carrés, cohérente avec les statistiques principales, et inclut donc un décentrage constant. Calcul par sommes glissantes.
- Interface : contraste des en-têtes d'onglets sélectionnés ; effacement des anciens résultats lors du chargement d'un fichier vide ou invalide ; remise à zéro des anciennes amplitudes et confiances lors d'une session trop courte. Une échelle de pixels absente ne produit plus des mesures prétendument en arcsecondes.
- Comparaison : toutes les sessions du fichier principal sont préparées en arrière-plan avant la comparaison ; les sessions non analysables ne sont plus assimilées à un guidage parfait à RMS zéro. Accès au dictionnaire des fichiers protégés lors des calculs de comparaison en arrière-plan.
- Exports : conservation des millisecondes dans le CSV ; numéro et date de début de session dans le JSON pour identifier l'analyse au sein d'un log multi-session.
- Packaging : génération des sources possible sans métadonnées Git, avec une liste explicite excluant les logs locaux, `bin`, `obj`, les instantanés d'audit et les archives. Les DLL et dépendances du plugin sont incluses ; les composants du programme hôte ne sont pas distribués comme dépendances du plugin.

## Validation

- Restauration des dépendances et compilation Release ; 53 tests portables réussis, avec les tests WPF sous culture française et les simulations indépendantes de rotation 3D.
- Six tests ajoutés ont d'abord échoué sur la version de départ, confirmant les défauts de statistiques, inversions DEC, période sur bruit, latitude sud, fenêtres RMS et lecture de sessions tronquées ; ils passent après correction. D'autres tests couvrent les défauts trouvés ensuite.
- Rejeu supplémentaire du log fourni : 16 sessions, 7 659 images. Comparaison exacte de chaque point reconstruit RA et DEC avec 1.0.0.4 ; continuité des raccordements DEC après dithering.
- Exécution du véritable contrôle WPF et de son template N.I.N.A. : sept onglets rendus et inspectés, zéro erreur de liaison WPF sur le scénario réel ; exports CSV et JSON relus.
- Contrôle NuGet en ligne, dépendances transitives incluses : aucun paquet vulnérable signalé par les sources consultées le jour de l'audit.
- Archives inspectées, versions et identité de DLL vérifiées ; manifeste de catalogue préparé à partir des métadonnées de la DLL contenue dans le ZIP, avec SHA256 du ZIP final et garde empêchant un changement d'identifiant.
- Une compilation et les tests depuis l'archive de sources extraite séparément vérifient la présence des fichiers nécessaires.

Les preuves locales (captures des onglets, export de test, copie initiale et comparaison) sont dans `artifacts/audit-2026-11-10/` et ne sont pas distribuées avec les sources.

## Limites avant publication

Les tests WPF ne remplacent pas le chargement du candidat dans une instance N.I.N.A. réelle. Aucun logiciel N.I.N.A., profil utilisateur ou équipement n'a été modifié ni commandé pendant l'audit. Un essai dans N.I.N.A., avec un profil sans connexion au matériel, reste à effectuer avant publication.

Une courbe sans guidage demeure une estimation fondée sur les distances et la calibration enregistrées. Les tests ne garantissent pas les déplacements effectifs d'une monture avec backlash, flexion ou glissement. Les attributions mécaniques des pics et les scores de confiance sont des heuristiques, pas des probabilités calibrées. Le calcul polaire doit être confronté à une mesure de dérive indépendante avant d'en suivre les directions de réglage.

Le dossier de travail actuel ne contient plus de métadonnées `.git` : les différences ont été contrôlées contre une copie initiale et les références de versions distribuées. Le ZIP de sources est fourni pour réintégration dans le dépôt, sans recréer ou publier son historique automatiquement.

Le service de comparaison multi-fichiers et ses propriétés de modèle sont présents, mais aucun contrôle de la vue actuelle ne les expose. L'export PDF n'a pas non plus de commande utilisable. Le README a été corrigé pour ne plus annoncer ces possibilités comme disponibles ; ajouter leurs interfaces constituerait un travail fonctionnel séparé.

## Reproduire et préparer la publication

Avec le SDK .NET 8 ou plus récent et PowerShell 7 :

```powershell
dotnet restore GuidingAnalyzer.sln
dotnet test Tests/GuidingAnalyzer.Tests.csproj -c Release --no-restore -p:DeployPluginToNina=false
dotnet build GuidingAnalyzer.sln -c Release --no-restore -p:DeployPluginToNina=false
./scripts/Package-Plugin.ps1
```

Fermer N.I.N.A., sauvegarder le dossier du plugin, puis extraire tout le ZIP preview dans `%LOCALAPPDATA%\NINA\Plugins\3.0.0\GuidingAnalyzer\`. Vérifier 1.0.0.5, charger le log, changer les valeurs Polar Alignment et cliquer Recalculate. Vérifier également une session courte, les FFT et les diagnostics sur la nuit. La correction de dérive doit agir uniquement sur RA.

Le manifeste ne doit être soumis qu'après publication et vérification du ZIP correspondant. Ne pas modifier le ZIP après calcul du checksum. Le script suivant lit les métadonnées dans l'archive et préserve l'identité du catalogue :

```powershell
./scripts/Prepare-NinaManifest.ps1 `
  -ArchivePath artifacts/GuidingAnalyzer-v1.0.0.5-preview.zip `
  -InstallerUrl https://github.com/jphf007/GuidingAnalyzer/releases/download/v1.0.0.5/GuidingAnalyzer-v1.0.0.5-preview.zip `
  -PreviousManifest CHEMIN_DU_MANIFESTE_ACTUEL_DU_CATALOGUE.json
```

La préparation locale ne publie aucune release et ne crée aucune demande de modification du catalogue.
