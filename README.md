# se7en Autoclick

**Télécharger :** [se7en-Autoclick.exe (Windows 10/11)](https://github.com/se7enclaude/se7en-autoclick/releases/latest/download/se7en-Autoclick.exe) — ou depuis **Seventh OS**.

Autoclic ultra précis et enregistreur de macros en boucle, pensé pour farmer sur Roblox.

## Autoclic
- De 1 clic par minute à **1 000 clics par seconde**, réglé au millième de seconde, sans dérive.
- Clic gauche, droit, molette, boutons 4/5, simple ou double — ou **une touche du clavier** à marteler.
- Aléatoire réglable (plus humain), durée d'appui, arrêt après N clics ou une durée.
- Sous le curseur, à un point fixe, ou à plusieurs points chacun leur tour.
- Raccourci en mode « marche / arrêt » ou « tant que je maintiens ».

## Macros
1. Appuie sur **F9** dans ton jeu : l'enregistrement démarre.
2. Joue normalement : touches, clics, molette et **mouvements de caméra** sont retenus.
3. Appuie sur **F9** pour arrêter, puis **F10** : tout est rejoué à l'identique, en boucle.

Vitesse ×0,25 à ×4, nombre de boucles ou durée, pause entre boucles, aléatoire, compte à rebours. Les touches partent avec leur
code matériel (ce que lit Roblox) et la souris en mouvements bruts (la caméra tourne pareil). Plusieurs macros nommées,
dans `%AppData%\Se7enAutoclick\Macros`.

## Pour farmer
- **Enchaînement** : plusieurs macros à la suite, chacune répétée (aller au spot ×1, farmer ×20, vendre ×1), en boucle.
- **Anti-AFK Roblox** : un petit geste toutes les X minutes, même quand Roblox est derrière une autre fenêtre.
- **Couleurs** : l'autoclic ne clique que quand un point de l'écran a (ou n'a plus) une couleur ; une macro peut attendre une couleur.
- **Éditeur** : commencer ou finir à une étape, supprimer une étape, changer un délai, annuler.

## Raccourcis et sécurité
Tous les raccourcis se changent dans Réglages : touche, combinaison (Ctrl, Maj, Alt) ou bouton de souris. Par défaut :
F6 autoclic, F9 enregistrer, F10 lire, F8 tout arrêter. **Échap maintenu 1 seconde** arrête tout, et la lecture s'arrête
si une autre fenêtre passe devant. Une pastille en haut de l'écran montre ce qui tourne.

## Développement
```
dotnet test tests/Se7enAutoclick.Tests
dotnet publish src/Se7enAutoclick -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/publish
python tools/make_icon.py dist
```

Certains jeux interdisent les macros dans leurs règles : à chacun de vérifier.
