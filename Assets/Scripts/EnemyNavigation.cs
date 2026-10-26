using Unity.AI.Navigation;
using UnityEngine;

// The test scene has no baked navigation asset yet. Build once before enemies start.
[DefaultExecutionOrder(-200)]
[RequireComponent(typeof(NavMeshSurface))]
public sealed class EnemyNavigation : MonoBehaviour
{
    public bool buildOnAwake = true;

    private void Awake()
    {
        NavMeshSurface surface = GetComponent<NavMeshSurface>();
        if (buildOnAwake && surface.navMeshData == null)
        {
            Physics.SyncTransforms();
            surface.BuildNavMesh();
        }
    }
}
