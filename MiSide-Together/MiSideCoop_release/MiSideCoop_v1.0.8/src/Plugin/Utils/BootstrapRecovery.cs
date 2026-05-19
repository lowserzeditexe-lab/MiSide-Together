using UnityEngine;

namespace MiSideCoop.Utils
{
    /// <summary>
    /// MonoBehaviour temporaire créé par ScenePatch lors de chaque LoadScene(Single).
    ///
    /// Mécanisme :
    ///   1. Créé dans la scène courante, passe en DontDestroyOnLoad → survit à la
    ///      transition de scène.
    ///   2. Dans Start() (contexte nouvelle scène), appelle EnsureBootstrap() pour
    ///      recréer le bootstrap co-op s'il a été détruit.
    ///   3. S'auto-détruit après usage (objet à usage unique).
    ///
    /// Enregistré dans IL2CPP via ClassInjector dans MiSideCoopPlugin.RegisterIl2CppTypes().
    /// </summary>
    public class BootstrapRecovery : MonoBehaviour
    {
        private void Awake()
        {
            // Garantit que l'objet est root avant DontDestroyOnLoad
            if (transform.parent != null) transform.SetParent(null);
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            // La nouvelle scène est active → vérifier / recréer le bootstrap
            MiSideCoopPlugin.EnsureBootstrap();

            // Auto-destruction : ce gardien n'est plus nécessaire
            Destroy(gameObject);
        }
    }
}
