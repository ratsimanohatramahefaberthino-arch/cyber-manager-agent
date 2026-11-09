import os

def generer_contexte():
    print("Génération du fichier GENERE_DEBUG_AGENT.txt...")
    fichier_sortie = "GENERE_DEBUG_AGENT.txt"
    
    fichiers_a_inclure = [
        "Program.cs",
        "ApiClient.cs",
        "AgentConfig.cs",
        # On inclut les fichiers de configuration pour vérifier les URLs
        "appsettings.json",
        "appsettings.Development.json",
    ]

    with open(fichier_sortie, "w", encoding="utf-8") as f:
        f.write("=" * 80 + "\n")
        f.write("   CODE SOURCE - AGENT C# (DEBUG RESEAU)\n")
        f.write("=" * 80 + "\n\n")
        
        for fichier in fichiers_a_inclure:
            if os.path.exists(fichier):
                f.write(f"\n{'=' * 80}\n")
                f.write(f"📄 {fichier}\n")
                f.write(f"{'=' * 80}\n\n")
                try:
                    with open(fichier, "r", encoding="utf-8") as source:
                        f.write(source.read())
                except UnicodeDecodeError:
                    with open(fichier, "r", encoding="latin-1") as source:
                        f.write(source.read())
                f.write("\n\n")
            else:
                print(f"⚠️ Fichier introuvable : {fichier}")

    print(f"✅ Terminé ! Le fichier {fichier_sortie} est prêt.")

if __name__ == "__main__":
    generer_contexte()