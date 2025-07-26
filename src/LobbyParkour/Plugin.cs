using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LobbyParkour;

[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;
        SceneManager.sceneLoaded += OnSceneLoaded;

        //new NativeDetour(
        //    AccessTools.Property(typeof(Mesh), "canAccess").GetMethod,
        //    ((Func<bool>)(() => true)).Method
        //).Apply();
        //new NativeDetour(
        //    AccessTools.Property(typeof(Mesh), "isReadable").GetMethod,
        //    ((Func<bool>)(() => true)).Method
        //).Apply();
    }

    Coroutine? AirportLoadCompleteCoroutine = null;

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Log.LogInfo($"Scene Loaded: path={scene.path}");
        if (scene is { path: "Assets/8_SCENES/Airport.unity" })
        {
            Log.LogInfo("started loading airport scene...");

            if (AirportLoadCompleteCoroutine is not null)
            {
                StopCoroutine(AirportLoadCompleteCoroutine);
                AirportLoadCompleteCoroutine = null;
            }

            // see `RunStarter` in peak code
            IEnumerator PatchAfterLoadFinished()
            {
                while (LoadingScreenHandler.loading)
                {
                    yield return null;
                }
                PatchAirportScene(scene);
            }

            AirportLoadCompleteCoroutine = StartCoroutine(PatchAfterLoadFinished());
        }
    }

    private void PatchAirportScene(Scene scene)
    {
        Log.LogInfo("finished loading airport scene, patching it now");

        int? terrainLayer = LayerMask.NameToLayer("Terrain") switch
        {
            -1 => null,
            var layer => layer,
        };

        void makeClimbable(GameObject target)
        {
            if (terrainLayer is int l)
            {
                target.layer = l;
            }

            Log.LogInfo($"makeClimbable({target})");
            if (target.GetComponent<MeshCollider>() is null && target.GetComponent<MeshFilter>() is MeshFilter meshFilter && meshFilter.sharedMesh is Mesh mesh)
            {
                var meshCollider = target.AddComponent<MeshCollider>();

                Log.LogInfo($"adding collider to {target} with mesh {meshFilter.sharedMesh}, which is (readable?{mesh.isReadable})");

                meshCollider.sharedMesh = Util.EnsureMeshReadable(mesh);
            }
        }

        new SceneTreeQueryNode.Epsilon()
            .Tap(n => n
                .Child(o => o is { name: "Map" })
                .Tee(
                    n => n
                        .Child(o => o is { name: "Flight Board" or "Flight Board (1)" or "Flight Board (2)" or "Flight Board (3)" or "Boarding Sign" }, makeClimbable),
                    n => n
                        .Child(o => o is { name: "BL_Airport" })
                        .Tee(
                            n => n.Child(o => o is { name: "OutofBoundsBlockers" or "OutofBoundsBlockers (1)" }, o => o.SetActive(false)),
                            n => n
                                .Child(o => o is { name: "Main Meshes" })
                                .Tee(
                                    n => n.Child(o => o is { name: "Schaffold" }, makeClimbable),
                                    // oob blockers near the climbing wall
                                    n => n.Child(o => o is { name: "Climbing wall blockers" }).Child(_ => true, o => o.SetActive(false)),
                                    // the entire roof
                                    n => n.Child(o => o is { name: "Airport (1)Roof" }).Child(_ => true, makeClimbable),
                                    // big gray corner pillars near the climbing wall
                                    n => n.Child(o => o is { name: "Cube" or "Cube (1)" }, makeClimbable),
                                    // carpet
                                    n => n.Child(o => o is { name: "Carpet" }, makeClimbable),
                                    n => n.Child(o => o is { name: "Outside grid" or "Outside grid (1)" }, makeClimbable)
                                ),
                            n => n.Child(o => o is { name: "Lights" }).Child(_ => true, makeClimbable),
                            n => n.Child(o => o is { name: "Displays" }).Child(_ => true, makeClimbable),
                            n => n.Child(o => o is { name: "GlassFence" }, makeClimbable),
                            // the planes outside
                            n => n.Child(o => o is { name: "Plane" or "Plane (1)" or "Plane (2)" }, makeClimbable).Child(_ => true, makeClimbable)
                        ),
                    n => n
                        .Child(o => o is { name: "Mirror (1)" })
                        .Tee(
                            n => n
                                .Child(o => o is { name: "Mirror" })
                                .Child(o => o is { name: "Mirror Collider" }, makeClimbable),
                            n => n
                                .Child(o => o is { name: "Details" })
                                .Child(o => o is { name: "Cube (10)" or "Cube (9)" or "Cube (2)" or "Cube (1)" }, makeClimbable)
                        )
                ))
            .Run(scene.GetRootGameObjects());
    }

    static class Util
    {
        public static IEnumerable<GameObject> GetChildrenOf(GameObject target) =>
            target.GetComponent<Transform>() is Transform transform
                ? (from Transform childTransform in transform select childTransform.gameObject)
                : [];

        public static Mesh EnsureMeshReadable(Mesh mesh) => mesh.isReadable ? mesh : MakeReadableMeshCopy(mesh);

        // via https://discussions.unity.com/t/reading-meshes-at-runtime-that-are-not-enabled-for-read-write/804189/8
        public static Mesh MakeReadableMeshCopy(Mesh nonReadableMesh)
        {
            Mesh meshCopy = new Mesh();
            meshCopy.indexFormat = nonReadableMesh.indexFormat;

            // Handle vertices
            GraphicsBuffer verticesBuffer = nonReadableMesh.GetVertexBuffer(0);
            int totalSize = verticesBuffer.stride * verticesBuffer.count;
            byte[] data = new byte[totalSize];
            verticesBuffer.GetData(data);
            meshCopy.SetVertexBufferParams(nonReadableMesh.vertexCount, nonReadableMesh.GetVertexAttributes());
            meshCopy.SetVertexBufferData(data, 0, 0, totalSize);
            verticesBuffer.Release();

            // Handle triangles
            meshCopy.subMeshCount = nonReadableMesh.subMeshCount;
            GraphicsBuffer indexesBuffer = nonReadableMesh.GetIndexBuffer();
            int tot = indexesBuffer.stride * indexesBuffer.count;
            byte[] indexesData = new byte[tot];
            indexesBuffer.GetData(indexesData);
            meshCopy.SetIndexBufferParams(indexesBuffer.count, nonReadableMesh.indexFormat);
            meshCopy.SetIndexBufferData(indexesData, 0, 0, tot);
            indexesBuffer.Release();

            // Restore submesh structure
            uint currentIndexOffset = 0;
            for (int i = 0; i < meshCopy.subMeshCount; i++)
            {
                uint subMeshIndexCount = nonReadableMesh.GetIndexCount(i);
                meshCopy.SetSubMesh(i, new SubMeshDescriptor((int)currentIndexOffset, (int)subMeshIndexCount));
                currentIndexOffset += subMeshIndexCount;
            }

            // Recalculate normals and bounds
            meshCopy.RecalculateNormals();
            meshCopy.RecalculateBounds();

            return meshCopy;
        }
    }

    class SceneTreeQueryNode(Predicate<GameObject>? Predicate = null, Action<GameObject>? Action = null)
    {
        readonly List<SceneTreeQueryNode> Children = [];

        public bool Matches(GameObject target) => Predicate?.Invoke(target) ?? true;

        protected virtual void Run(GameObject target, int depth)
        {
            // Log.LogInfo($"query run: {new String(' ', depth * 2)} {target}");
            Action?.Invoke(target);

            foreach ((GameObject childGameObject, SceneTreeQueryNode childNode) in
                from GameObject childGameObject in Util.GetChildrenOf(target)
                from SceneTreeQueryNode childNode in Children
                where childNode.Matches(childGameObject)
                select (childGameObject, childNode))
            {
                childNode.Run(childGameObject);
            }
        }

        public void Run(GameObject target) => Run(target, 0);

        public void Run(IEnumerable<GameObject> targets)
        {
            foreach (var target in targets)
            {
                Run(target);
            }
        }

        public SceneTreeQueryNode Tap(Action<SceneTreeQueryNode> f)
        {
            f(this);
            return this;
        }

        public SceneTreeQueryNode Child(SceneTreeQueryNode child)
        {
            this.Children.Add(child);
            return child;
        }

        public SceneTreeQueryNode Child(Predicate<GameObject> predicate) => Child(new SceneTreeQueryNode(predicate));
        public SceneTreeQueryNode Child(Predicate<GameObject> predicate, Action<GameObject> action) => Child(new SceneTreeQueryNode(predicate, action));


        public void Tee(params Action<SceneTreeQueryNode>[] tees)
        {
            foreach (var tee in tees)
            {
                tee(this);
            }
        }

        /// <summary> zero-length match </summary>
        public class Epsilon : SceneTreeQueryNode
        {
            public Epsilon() : base() { }
            protected override void Run(GameObject target, int depth)
            {
                foreach (var child in Children) child.Run(target, depth);
            }
        }
    }
}
