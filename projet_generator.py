import os

def generer_contexte_agent():
    print("Génération du fichier GENERE_AGENT.txt (Agent C#)...")
    fichier_sortie = "GENERE_AGENT.txt"
    
    # On enlève le préfixe 'cyber-manager-agent/' car le script est déjà exécuté dans ce dossier
    fichiers_a_inclure = [
        "cyber-manager-agent.csproj",
        "Program.cs",
        "appsettings.json",
        "appsettings.Development.json",
        "Properties/launchSettings.json",
        "structureAgent.txt"
    ]

    with open(fichier_sortie, "w", encoding="utf-8") as f:
        f.write("==================================================\n")
        f.write("   CODE SOURCE COMPLET - AGENT CLIENT C# (.NET)   \n")
        f.write("==================================================\n\n")
        
        for fichier in fichiers_a_inclure:
            if os.path.exists(fichier):
                f.write(f"\n{'='*80}\n")
                f.write(f"📄 {fichier}\n")
                f.write(f"{'='*80}\n\n")
                with open(fichier, "r", encoding="utf-8") as source:
                    f.write(source.read())
                f.write("\n\n")
            else:
                print(f"⚠️ Fichier introuvable : {fichier}")

    print(f"✅ Terminé ! Le fichier {fichier_sortie} est prêt.")

if __name__ == "__main__":
    generer_contexte_agent()