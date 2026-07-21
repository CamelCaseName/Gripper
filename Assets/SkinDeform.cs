using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using UnityEngine;

public class SkinDeform : MonoBehaviour
{
    //todo add option to preprocess meshcollider mesh for non-convex meshes to check the thickness behind each vertex -> max depth
    //todo preprocess meshcollider mesh to find all nearby vertices and store
    //them in a list so we can calculate the sphere size needed to be close enough to the vertex
    private bool isSkinned = false;
    private bool prevsetAnyVertex = false;
    private const float jellyBuildup = 0.05f;
    private const int CollTypeSphere = 0;
    private const int CollTypeBox = 1;
    private const int CollTypeMesh = 2;
    private const int CollTypeCapsule = 3;
    private ExampleVertex[] verticesF;
    private float changePerVert = 0;
    private float dragscale = 0;
    private float[] jellyTouchTimes;
    private float[] touchtimes;
    private GraphicsBuffer vertBuffer = null;
    private int dupeVertCount = 0;
    private int touchCount = 0;
    private int[,] identicalVerts;
    private Mesh _mesh;
    private MeshFilter filter = null;
    private readonly bool isAvxSupported = X86.Avx.IsAvxSupported;
    private readonly bool isSSE2Supported = X86.Sse2.IsSse2Supported;
    private readonly float[] differences = new float[6];
    private static readonly Dictionary<string, List<Collider>> colliders = new();
    private static readonly Dictionary<string, List<int>> collType = new();
    private static readonly Dictionary<string, List<Vector3>> oldCollPos = new();
    private static readonly Dictionary<string, List<Vector3>> collVerts = new();
    private static readonly Dictionary<string, List<Vector3>> collNormals = new();
    private static readonly Dictionary<string, List<float[]>> meshCollThickness = new();
    private static readonly Dictionary<string, List<float[]>> meshVertCheckRadius = new();
    private readonly List<Vector3> normals = new();
    private readonly List<Vector3> vertices = new();
    private readonly Vector3 zero = Vector3.zero;
    private Vector3 totalTouchValue = Vector3.zero;
    private Vector3[] fakeVolumeVertexOffsets;
    private Vector3[] jellyVertexOffsets;
    private Vector3[] oldNormals;
    private Vector3[] oldVertices;
    private Vector3[] vertexOffsets;
    private static readonly Dictionary<string, bool> InSetup = new();
    private Mesh Mesh
    {
        get
        {
            if (_mesh == null)
            {
                if (renderer is SkinnedMeshRenderer srender)
                {
                    _mesh = srender.sharedMesh;
                }
                else if (renderer.GetType() == typeof(MeshRenderer))
                {
                    if (filter == null)
                    {
#pragma warning disable UNT0039 // Use RequireComponent attribute when self-invoking GetComponent
                        filter = GetComponent<MeshFilter>();
#pragma warning restore UNT0039 // Use RequireComponent attribute when self-invoking GetComponent
                    }
                    _mesh = filter.sharedMesh;
                }
            }
            return _mesh;
            throw new InvalidDataException("no MeshRenderer or SkinnedMeshRenderer present on the object");
        }
    }

    #region properties
    [Tooltip("Plug the objects renderer into here")]
    public Renderer renderer = null;

    [Tooltip("Colliders with this Tag will be respected for collision")]
    public string colliderTag = "SkinColliders";

    [Min(0)]
    [Tooltip("This defines how deep the mesh is deformed. 1 = whole length. Can be multiple")]
    public float MaxIndent = 0.03f;

    [Min(0)]
    [Tooltip("This defines how fast the mesh is deformed. 1 = instant")]
    public float IndentScale = 0.7f;

    [Min(0)]
    [Tooltip("This defines how fast the mesh is reformed in seconds. 0 = instant")]
    public float RecoveryDelayTime = 0.1f;

    [Min(0)]
    [Tooltip("This defines how long to wait until the mesh is reformed, in seconds. 0 = instant")]
    public float IndentRecoveryTime = 0.7f;

    [Min(0)]
    [Tooltip("This defines how much further the jelly effect or dragging is triggered. 1 = colliders size doubled. 0 = turned off.")]
    public float EffectRadius = 0.15f;

    [Tooltip("If this is turned on, the jelly effect is used")]
    public bool JellyEnabled = false;

    [Min(0)]
    [Tooltip("This defines how much the jelly effect jumps up depending on the colliders speed")]
    public float JellyStrength = 1.1f;

    [Min(0)]
    [Tooltip("This defines how long to wait until the jelly settles. 0 = instant")]
    public float JellyRecoveryTime = 0.5f;

    [Min(0)]
    [Tooltip("This defines how fast collider has to move in units/frame for the jelly effect to trigger. 0 = on any movement")]
    public float VelocityThreshhold = 0.03f;

    [Tooltip("If this is turned on, the object expands on indentation on every untouched vertex")]
    public bool FakeVolumeEnabled = false;

    [Tooltip("This sets how much the object expands on indentation on every untouched vertex. Can be negative to have it shrink on touch")]
    public float FakeVolumeScale = 1f;

    [Tooltip("If this is turned on, the objects mesh is scanned for duplicate vertices which will then be handled the same way -> no tears")]
    public bool FixDuplicateVertices = false;

    [Tooltip("If this is turned on, the collider also slightly drags the vertices on collision")]
    public bool DragEnabled = true;

    [Min(0)]
    [Tooltip("This sets how strongly the collider drags the object")]
    public float DragScale = 1f;

    [Tooltip("If this is turned on, all meshcolliders calculate the thickness behind each vertex to improve collision accuracy")]
    public bool MeshThickness = true;
    #endregion properties

    // register active colliders here
    private void Awake()
    {
        UpdateColliders(colliderTag, MeshThickness);

        if (renderer is SkinnedMeshRenderer)
        {
            isSkinned = true;
        }

        GetVertices();

        int count = vertices.Count;
        oldVertices = new Vector3[count];
        oldNormals = new Vector3[count];
        //touchNormals = new Vector3[vertices.Count];
        vertices.CopyTo(oldVertices, 0);
        normals.CopyTo(oldNormals, 0);
        vertexOffsets = new Vector3[count];
        jellyVertexOffsets = new Vector3[count];
        fakeVolumeVertexOffsets = new Vector3[count];
        touchtimes = new float[count];
        jellyTouchTimes = new float[count];
        if (!FixDuplicateVertices || isSkinned)
        {
            return;
        }
        SetUpMeshFixes();
    }

    public static void UpdateColliders(string _colliderTag, bool meshThicknessEnabled)
    {
        if (!InSetup.ContainsKey(_colliderTag))
        {
            InSetup.Add(_colliderTag, true);
        }
        else if (InSetup[_colliderTag])
        {
            //already set this tag up
            return;
        }
        if (colliders.ContainsKey(_colliderTag))
        {
            colliders[_colliderTag].Clear();
            collType[_colliderTag].Clear();
            oldCollPos[_colliderTag].Clear();
            meshCollThickness[_colliderTag].Clear();
            meshVertCheckRadius[_colliderTag].Clear();
            collVerts[_colliderTag].Clear();
            collNormals[_colliderTag].Clear();
        }
        else
        {
            colliders.Add(_colliderTag, new());
            collType.Add(_colliderTag, new());
            oldCollPos.Add(_colliderTag, new());
            meshCollThickness.Add(_colliderTag, new());
            meshVertCheckRadius.Add(_colliderTag, new());
            collVerts.Add(_colliderTag, new());
            collNormals.Add(_colliderTag, new());
        }
        List<Collider> colliders1 = colliders[_colliderTag];
        List<int> collType1 = collType[_colliderTag];
        List<Vector3> oldCollPos1 = oldCollPos[_colliderTag];
        foreach (var go in GameObject.FindGameObjectsWithTag(_colliderTag))
        {
            if (go.TryGetComponent<SphereCollider>(out var coll))
            {
                colliders1.Add(coll);
                collType1.Add(CollTypeSphere);
                oldCollPos1.Add(coll.transform.position);
            }
            if (go.TryGetComponent<BoxCollider>(out var box))
            {
                colliders1.Add(box);
                collType1.Add(CollTypeBox);
                oldCollPos1.Add(box.transform.position);
            }
            if (go.TryGetComponent<MeshCollider>(out var m))
            {
                colliders1.Add(m);
                collType1.Add(CollTypeMesh);
                oldCollPos1.Add(m.transform.position);
                SetUpMeshCollider(m, _colliderTag, meshThicknessEnabled);
            }
            if (go.TryGetComponent<CapsuleCollider>(out var c))
            {
                colliders1.Add(c);
                collType1.Add(CollTypeCapsule);
                oldCollPos1.Add(c.transform.position);
            }
        }
    }

    private static void SetUpMeshCollider(MeshCollider m, string tag, bool meshThicknessEnabled)
    {
        m.sharedMesh.GetVertices(collVerts[tag]);
        var verts = collVerts[tag];
        m.sharedMesh.GetNormals(collNormals[tag]);
        var normals = collNormals[tag];
        meshVertCheckRadius[tag].Add(new float[verts.Count]);
        var checkRadii = meshVertCheckRadius[tag];
        meshCollThickness[tag].Add(new float[verts.Count]);
        var thicknesses = meshCollThickness[tag];
        var tris = m.sharedMesh.triangles;
        List<float> radii = new();
        HashSet<int> triVerts = new();
        for (int i = 0; i < verts.Count; i++)
        {
            //sert up test radius
            radii.Clear();
            Vector3 vert = verts[i];
            //gather all vertex indices of verts connected to this one
            for (int j = 0; j < tris.Length; j += 3)
            {
                if (i == j / 3)
                {
                    continue;
                }
                if (tris[j] == i)
                {
                    triVerts.Add(tris[j + 1]);
                    triVerts.Add(tris[j + 2]);
                }
                else if (tris[j + 1] == i)
                {
                    triVerts.Add(tris[j]);
                    triVerts.Add(tris[j + 2]);
                }
                else if (tris[j + 2] == i)
                {
                    triVerts.Add(tris[j]);
                    triVerts.Add(tris[j + 1]);
                }
            }
            //calculate all triangle line lengths and use second longest as radius
            var trilist = triVerts.ToList();
            for (int v = 0; v < triVerts.Count; v++)
            {
                radii.Add((verts[trilist[v]] - vert).sqrMagnitude);
            }
            radii.Sort();
            //we need at least 3 verts for a tri, so 2 in this list oooor none
            if (radii.Count >= 2)
            {
                checkRadii[^1][i] = Mathf.Sqrt(radii[^2]);
            }
            else
            {
                //can this ever happen?
                checkRadii[^1][i] = 0;
            }

            //set up thickness here -> go through each other vert and find the one closest to the
            //inverse normal and also closest to the vert, which should! be the one opposite in the model
            //todo maybe switch over to checking triangle collision??
            //Vector3 pNorm = (Vector3.Cross(p1-p0, p2-p0)).normalized;
            //Vector3 pMid = (p0 + p1 + p2) / 3;
            if (meshThicknessEnabled)
            {
                float smallestNorm = float.MaxValue;
                float smallestdist = float.MaxValue;
                var norm = -normals[i];
                for (int x = 0; x < verts.Count; x++)
                {
                    if (x == i)
                    {
                        continue;
                    }
                    Vector3 other = verts[x];
                    Vector3 distVert = other - vert;
                    var normDist = Vector3.Dot(distVert, norm);
                    if (normDist > 0)
                    {
                        var dist = distVert.magnitude;
                        if (normDist < smallestNorm && dist < smallestdist)
                        {
                            smallestNorm = normDist;
                            smallestdist = dist;
                        }
                    }
                }
                thicknesses[^1][i] = smallestNorm;
            }
        }
    }

    private void SetUpMeshFixes()
    {
        int maxSimilarCount = 0;
        List<List<int>> indices = new();
        for (int i = 0; i < vertices.Count; i++)
        {
            int similarCount = 0;
            Vector3 vert = vertices[i];
            for (int j = i + 1; j < vertices.Count; j++)
            {
                while (j >= indices.Count)
                {
                    indices.Add(new());
                }
                if (vert == vertices[j])
                {
                    indices[i].Add(j);
                    indices[j].Add(i);
                    similarCount++;
                }
            }
            dupeVertCount += similarCount;
            maxSimilarCount = Math.Max(similarCount, maxSimilarCount);
        }
        identicalVerts = new int[vertices.Count, maxSimilarCount];
        for (int i = 0; i < vertices.Count; i++)
        {
            for (int x = 0; x < indices[i].Count; x++)
            {
                identicalVerts[i, x] = indices[i][x];
            }
        }
    }

    //todo add option for wave to travel along vertices, "just" needs to know the next vertex in a direction, no idea what data structure to use there
    //then we can add a force and have it travel along all edges towards a direction.
    //todo for gpu: use vertex shader and keep offsets and stuff on gopu, only supply collider data each frame
    //todo change to computeshader on toggle so the user can choose between CPU and GPU load
    private void Update()
    {
        InSetup[colliderTag] = false;
        GetVertices();
        if (vertices.Count == 0)
        {
            return;
        }
        bool setAnyVertex = false;
        float currentTime = Time.time;
        var sindentscale = IndentScale;
        var t = transform;
        Vector3 tlocalScale = t.localScale;
        var sjellystrength = JellyStrength;
        float sjelly = (1 + EffectRadius);
        var smaxIdent = MaxIndent / tlocalScale.magnitude;
        totalTouchValue = zero;
        touchCount = 0;
        dragscale = DragEnabled ? DragScale : 0;
        var thisColliders = colliders[colliderTag];
        var thisColliderTypes = collType[colliderTag];
        var thisColliderPos = oldCollPos[colliderTag];
        var verts = collVerts[colliderTag];
        var normals = collNormals[colliderTag];
        var radii = meshVertCheckRadius[colliderTag];
        var thickness = meshCollThickness[colliderTag];
        var collCount = thisColliders.Count;
        int m = 0;
        for (int c = 0; c < collCount; c++)
        {
            Collider coll = thisColliders[c];
            if (!coll.bounds.Intersects(renderer.bounds)) //dont need scaled bounds here as the up wave can only start once we press into the obj
            {
                thisColliderPos[c] = coll.transform.position;
                continue;
            }

            Transform collT = coll.transform;
            Vector3 velVec = collT.position - thisColliderPos[c];
            velVec = t.InverseTransformVector(velVec);
            var vel = velVec.magnitude;
            //move collider to local space
            var transformedColl = t.InverseTransformPoint(collT.position);

            //0 = sphere, 1 = box, 2 = mesh
            int coltype = thisColliderTypes[c];
            if (coltype == CollTypeSphere)
            {
                HandleSphereCollider(ref setAnyVertex, coll, vel, velVec, transformedColl);
            }
            else if (coltype == CollTypeBox)
            {
                HandleBoxColl(ref setAnyVertex, coll, collT, vel, velVec, transformedColl);
            }
            else if (coltype == CollTypeMesh)
            {
                //check if other vert is on the correct side with inverse normal of collider mesh, then check if close enought and move away
                var meshColl = (MeshCollider)coll;
                var collLocalScale = collT.localScale * sjelly;
                var extents = coll.bounds.extents * sjelly;
                //var scaledSizeX = (collLocalScale.x / tlocalScale.x) * (EffectRadius) / 4;
                //var scaledSizeY = (collLocalScale.y / tlocalScale.y) * (EffectRadius) / 4;
                //var scaledSizeZ = (collLocalScale.z / tlocalScale.z) * (EffectRadius) / 4;
                //transform the local axes
                var axisX = t.InverseTransformVector(extents.x, 0, 0);
                var axisY = t.InverseTransformVector(0, extents.y, 0);
                var axisZ = t.InverseTransformVector(0, 0, extents.z);

                //sum their absolute value to get the world extents
                extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
                extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
                extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

                velVec *= -dragscale;

                //angle is finally correct when scaled.
                //todo collider becomes a little smaller the more the mesh is stretched non uniformly
                Matrix4x4 ltwtranspose = t.localToWorldMatrix.transpose;
                Vector3 collPos = collT.position;

                //bruh we are caching it before the loop unity grrr
                meshColl.sharedMesh.GetVertices(verts);
                meshColl.sharedMesh.GetNormals(normals);
                var avgcollscale = ((collLocalScale.x / tlocalScale.x) + (collLocalScale.y / tlocalScale.y) + (collLocalScale.z / tlocalScale.z)) / 3;

                int length = verts.Count;
                var count = vertices.Count;
                for (int v = 0; v < length; v++)
                {
                    Vector3 collVert = verts[v];
                    var collVertNormal = ltwtranspose.MultiplyVector(normals[v]);
                    collVertNormal.x *= tlocalScale.x;
                    collVertNormal.y *= tlocalScale.y;
                    collVertNormal.z *= tlocalScale.z;
                    collVertNormal = t.rotation * collVertNormal.normalized * (collLocalScale.y / 2);
                    var collVertP = t.InverseTransformPoint(collPos - collVert - collVertNormal);
                    var collVertN = (sindentscale) * (collVertP - transformedColl).normalized;

                    float collVertRadius = radii[m][v] * avgcollscale;
                    float collVertThickness = thickness[m][v] * avgcollscale;
                    float checkSizeX = transformedColl.x - collVertRadius;
                    float checkSizeXP = transformedColl.x + collVertRadius;
                    float checkSizeY = transformedColl.y - collVertRadius;
                    float checkSizeYP = transformedColl.y + collVertRadius;
                    float checkSizeZ = transformedColl.z - collVertRadius;
                    float checkSizeZP = transformedColl.z + collVertRadius;

                    //when the mesh is scaled not uniformly, the collider vector length is not correct
                    for (int i = 0; i < count; i++)
                    {
                        var vert = vertices[i];
                        if (checkSizeX >= vert.x || vert.x >= checkSizeXP)
                        {
                            continue;
                        }
                        if (checkSizeY >= vert.y || vert.y >= checkSizeYP)
                        {
                            continue;
                        }
                        if (checkSizeZ >= vert.z || vert.z >= checkSizeZP)
                        {
                            continue;
                        }
                        if (vertexOffsets[i].sqrMagnitude >= smaxIdent || (EffectRadius != 0 && jellyVertexOffsets[i].sqrMagnitude >= smaxIdent))
                        {
                            continue;
                        }
                        if (ShouldSkipVert(i))
                        {
                            continue;
                        }
                        //calculate closes face of our collider
                        //point has to have 6x  to be inside of our collider
                        var diff = Vector3.Dot(collVertN, vert - collVertP);
                        if (diff >= 0)
                        {
                            continue;
                        }
                        float jelly = collVertThickness + diff;
                        float checksize = collVertThickness;
                        float collidersize = diff;
                        if (jelly <= 0 || sjelly == 1)
                        {
                            float scale = sindentscale * jelly;
                            Vector3 move = (scale * collVertN) + velVec;
                            if (isSkinned)
                            {
                                vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * move;
                            }
                            else
                            {
                                vertexOffsets[i] -= move;
                            }
                            touchtimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                        if (jelly > 0)
                        {
                            if (JellyEnabled
                            && vertexOffsets[i] == zero
                            && vel > VelocityThreshhold)
                            {
                                //this works and is fine
                                jellyVertexOffsets[i] = normals[i] * (jelly * (sindentscale * sjellystrength * (1 + vel)));
                                jellyTouchTimes[i] = currentTime;
                                setAnyVertex = true;
                            }
                            else if (DragEnabled)
                            {
                                var move = (velVec * (dragscale * ((checksize - jelly) / (checksize - collidersize))));
                                if (isSkinned)
                                {
                                    vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * move;
                                }
                                else
                                {
                                    vertexOffsets[i] += move;
                                }
                                touchtimes[i] = currentTime;
                                setAnyVertex = true;
                            }
                        }

                        continue;
                    }
                }
                m++;
            }
            else if (coltype == CollTypeCapsule)
            {
                HandleCapsuleColl(ref setAnyVertex, coll, collT, velVec, vel, transformedColl);
            }
            thisColliderPos[c] = collT.position;
        }

        //if we have not set anything this and prev frame why bother
        if (!prevsetAnyVertex && !setAnyVertex)
        {
            return;
        }

        if (FixDuplicateVertices && identicalVerts is not null)
        {
            for (int x = 0; x < vertexOffsets.Length; x++)
            {
                if (ShouldSkipVertImpl(x))
                {
                    continue;
                }
                setAnyVertex = ResetOffsets(setAnyVertex, currentTime, x);
            }
        }
        else
        {
            for (int x = 0; x < vertexOffsets.Length; x++)
            {
                setAnyVertex = ResetOffsets(setAnyVertex, currentTime, x);
            }
        }

        if (FakeVolumeEnabled && setAnyVertex)
        {
            HandleFakeVolume(currentTime);
        }

        setAnyVertex |= changePerVert > 0;
        if (setAnyVertex)
        {
            SetVertices();
        }
        else
        {
            //only recalculate after were done manipulating
            if (prevsetAnyVertex)
            {
                Mesh.RecalculateNormals();
                Mesh.RecalculateBounds();
            }
        }
        prevsetAnyVertex = setAnyVertex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool HandleSphereCollider(ref bool setAnyVertex, Collider coll, float vel, Vector3 velVec, Vector3 transformedColl)
        {
            var tbounds = coll.bounds.extents;

            var checksizeX = (tbounds.x * (1 + EffectRadius) / tlocalScale.x);
            float negCheckSizeX = transformedColl.x - checksizeX;
            checksizeX += transformedColl.x;

            var checksizeY = (tbounds.y * (1 + EffectRadius) / tlocalScale.y);
            float negCheckSizeY = transformedColl.y - checksizeY;
            checksizeY += transformedColl.y;

            var checksizeZ = (tbounds.z * (1 + EffectRadius) / tlocalScale.z);
            float negCheckSizeZ = transformedColl.z - checksizeZ;
            checksizeZ += transformedColl.z;

            var collSize = ((tbounds.x / tlocalScale.x) + (tbounds.y / tlocalScale.y) + (tbounds.z / tlocalScale.z)) / 3;
            var checksize = (checksizeX + checksizeY + checksizeZ) / 3;
            velVec *= dragscale;
            var count = vertices.Count;
            bool skipEnable = FixDuplicateVertices && identicalVerts is not null;
            for (int i = 0; i < count; i++)
            {
                var vert = vertices[i];
                if (vert.x > checksizeX || vert.x < negCheckSizeX)
                {
                    continue;
                }
                if (vert.y > checksizeY || vert.y < negCheckSizeY)
                {
                    continue;
                }
                if (vert.z > checksizeZ || vert.z < negCheckSizeZ)
                {
                    continue;
                }
                if (vertexOffsets[i].sqrMagnitude >= smaxIdent || (EffectRadius != 0 && jellyVertexOffsets[i].sqrMagnitude >= smaxIdent))
                {
                    touchtimes[i] = currentTime;
                    continue;
                }

                if (skipEnable && ShouldSkipVertImpl(i))
                {
                    continue;
                }

                vert -= transformedColl;

                var diffLength = vert.magnitude;
                if (diffLength < collSize)
                {
                    float scale = ((collSize - diffLength) / collSize) * sindentscale;
                    var move = ((vert * scale) + velVec);
                    if (isSkinned)
                    {
                        vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * move;
                    }
                    else
                    {
                        vertexOffsets[i] += move;
                    }
                    touchtimes[i] = currentTime;
                    setAnyVertex = true;
                }
                else if (EffectRadius > 0
                    && diffLength > collSize
                    && diffLength < checksize)
                {
                    if (JellyEnabled
                        && vertexOffsets[i] == zero
                        && vel > VelocityThreshhold)
                    {
                        jellyVertexOffsets[i] = normals[i] * (-1 * (diffLength - checksize) * (sindentscale * sjellystrength * (1 + vel)));
                        jellyTouchTimes[i] = currentTime;
                        setAnyVertex = true;
                    }
                    else if (DragEnabled)
                    {
                        var move = (velVec * (dragscale * ((checksize - diffLength) / (checksize - collSize))));
                        if (isSkinned)
                        {
                            vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * move;
                        }
                        else
                        {
                            vertexOffsets[i] += move;
                        }
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                    }
                }
            }

            return setAnyVertex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool HandleBoxColl(ref bool setAnyVertex, Collider coll, Transform collT, float vel, Vector3 velVec, Vector3 transformedColl)
        {
            var collLocalScale = collT.localScale * sjelly;
            var extents = coll.bounds.extents * sjelly;
            var scaledSizeX = (collLocalScale.x / tlocalScale.x) * (EffectRadius) / 4;
            var scaledSizeY = (collLocalScale.y / tlocalScale.y) * (EffectRadius) / 4;
            var scaledSizeZ = (collLocalScale.z / tlocalScale.z) * (EffectRadius) / 4;

            //angle is finally correct when scaled.
            //todo collider becomes a little smaller the more the mesh is stretched non uniformly - same issue as capsule and box
            Matrix4x4 ltwtranspose = t.localToWorldMatrix.transpose;
            Vector3 collPos = collT.position;
            var gboxRight = ltwtranspose.MultiplyVector(collT.right);
            gboxRight.x *= tlocalScale.x;
            gboxRight.y *= tlocalScale.y;
            gboxRight.z *= tlocalScale.z;
            gboxRight = t.rotation * gboxRight.normalized * (collLocalScale.x / 2);
            var boxRightP = t.InverseTransformPoint(collPos + gboxRight);
            var boxLeftP = t.InverseTransformPoint(collPos - gboxRight);

            var gboxUp = ltwtranspose.MultiplyVector(collT.up);
            gboxUp.x *= tlocalScale.x;
            gboxUp.y *= tlocalScale.y;
            gboxUp.z *= tlocalScale.z;
            gboxUp = t.rotation * gboxUp.normalized * (collLocalScale.y / 2);
            var boxUpP = t.InverseTransformPoint(collPos + gboxUp);
            var boxDownP = t.InverseTransformPoint(collPos - gboxUp);

            var gboxForward = ltwtranspose.MultiplyVector(collT.forward);
            gboxForward.x *= tlocalScale.x;
            gboxForward.y *= tlocalScale.y;
            gboxForward.z *= tlocalScale.z;
            gboxForward = t.rotation * gboxForward.normalized * (collLocalScale.z / 2);
            var boxForwardP = t.InverseTransformPoint(collPos + gboxForward);
            var boxBackP = t.InverseTransformPoint(collPos - gboxForward);

            var boxForwardN = sindentscale * (boxForwardP - transformedColl).normalized;
            var boxBackN = -boxForwardN;
            var boxRightN = sindentscale * (boxRightP - transformedColl).normalized;
            var boxLeftN = -boxRightN;
            var boxUpN = sindentscale * (boxUpP - transformedColl).normalized;
            var boxDownN = -boxUpN;

            //transform the local axes
            var axisX = t.InverseTransformVector(extents.x, 0, 0);
            var axisY = t.InverseTransformVector(0, extents.y, 0);
            var axisZ = t.InverseTransformVector(0, 0, extents.z);

            //sum their absolute value to get the world extents
            extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            //when the mesh is scaled not uniformly, the collider vector length is not correct
            var count = vertices.Count;
            velVec *= -dragscale;
            float checkSizeX = transformedColl.x - extents.x;
            float checkSizeXP = transformedColl.x + extents.x;
            float checkSizeY = transformedColl.y - extents.y;
            float checkSizeYP = transformedColl.y + extents.y;
            float checkSizeZ = transformedColl.z - extents.z;
            float checkSizeZP = transformedColl.z + extents.z;
            for (int i = 0; i < count; i++)
            {
                var vert = vertices[i];
                if (checkSizeX >= vert.x || vert.x >= checkSizeXP)
                {
                    continue;
                }
                if (checkSizeY >= vert.y || vert.y >= checkSizeYP)
                {
                    continue;
                }
                if (checkSizeZ >= vert.z || vert.z >= checkSizeZP)
                {
                    continue;
                }
                if (vertexOffsets[i].sqrMagnitude >= smaxIdent || (EffectRadius != 0 && jellyVertexOffsets[i].sqrMagnitude >= smaxIdent))
                {
                    continue;
                }
                if (ShouldSkipVert(i))
                {
                    continue;
                }
                //calculate closes face of our collider
                //point has to have 6x  to be inside of our collider
                var diffDown = Vector3.Dot(boxDownN, vert - boxDownP);
                if (diffDown < 0)
                {
                    differences[0] = diffDown;
                }
                else
                {
                    continue;
                }
                var diffForward = Vector3.Dot(boxForwardN, vert - boxForwardP);
                if (diffForward < 0)
                {
                    differences[1] = diffForward;
                }
                else
                {
                    continue;
                }
                var diffBack = Vector3.Dot(boxBackN, vert - boxBackP);
                if (diffBack < 0)
                {
                    differences[2] = diffBack;
                }
                else
                {
                    continue;
                }
                var diffRight = Vector3.Dot(boxRightN, vert - boxRightP);
                if (diffRight < 0)
                {
                    differences[3] = diffRight;
                }
                else
                {
                    continue;
                }
                var diffLeft = Vector3.Dot(boxLeftN, vert - boxLeftP);
                if (diffLeft < 0)
                {
                    differences[4] = diffLeft;
                }
                else
                {
                    continue;
                }
                var diffUp = Vector3.Dot(boxUpN, vert - boxUpP);
                if (diffUp < 0)
                {
                    differences[5] = diffUp;
                }
                else
                {
                    continue;
                }
                var min = Mathf.Max(differences);
                if (min >= 0)
                {
                    continue;
                }
                float jelly = -1;
                float checksize = 0;
                float collidersize = 0;
                //get the side corrected shove away vector
                if (min == diffDown)
                {
                    jelly = scaledSizeY + diffDown;
                    checksize = scaledSizeY;
                    collidersize = diffDown;
                    setAnyVertex = SetVertOffset(boxDownN, velVec, i, jelly);
                }
                else if (min == diffForward)
                {
                    jelly = scaledSizeZ + diffForward;
                    checksize = scaledSizeZ;
                    collidersize = diffForward;
                    setAnyVertex = SetVertOffset(boxForwardN, velVec, i, jelly);
                }
                else if (min == diffBack)
                {
                    jelly = scaledSizeZ + diffBack;
                    checksize = scaledSizeZ;
                    collidersize = diffBack;
                    setAnyVertex = SetVertOffset(boxBackN, velVec, i, jelly);
                }
                else if (min == diffRight)
                {
                    jelly = scaledSizeX + diffRight;
                    checksize = scaledSizeX;
                    collidersize = diffRight;
                    setAnyVertex = SetVertOffset(boxRightN, velVec, i, jelly);
                }
                else if (min == diffLeft)
                {
                    jelly = scaledSizeX + diffLeft;
                    checksize = scaledSizeX;
                    collidersize = diffLeft;
                    setAnyVertex = SetVertOffset(boxLeftN, velVec, i, jelly);
                }
                else if (min == diffUp)
                {
                    jelly = scaledSizeY + diffUp;
                    checksize = scaledSizeY;
                    collidersize = diffUp;
                    setAnyVertex = SetVertOffset(boxUpN, velVec, i, jelly);
                }
                if (jelly > 0)
                {
                    if (JellyEnabled
                    && vertexOffsets[i] == zero
                    && vel > VelocityThreshhold)
                    {
                        //this works and is fine
                        jellyVertexOffsets[i] = normals[i] * (jelly * (sindentscale * sjellystrength * (1 + vel)));
                        jellyTouchTimes[i] = currentTime;
                        setAnyVertex = true;
                    }
                    else if (DragEnabled)
                    {
                        var move = (velVec * (dragscale * ((checksize - jelly) / (checksize - collidersize))));
                        if (isSkinned)
                        {
                            vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * move;
                        }
                        else
                        {
                            vertexOffsets[i] += move;
                        }
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                    }
                }

                continue;
            }

            return setAnyVertex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool SetVertOffset(Vector3 dir, Vector3 velVec, int i, float jellydist)
        {
            if (jellydist <= 0 || sjelly == 1)
            {
                float scale = sindentscale * jellydist;
                Vector3 move = (scale * dir) + velVec;
                if (isSkinned)
                {
                    vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * move;
                }
                else
                {
                    vertexOffsets[i] -= move;
                }
                touchtimes[i] = currentTime;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool HandleCapsuleColl(ref bool setAnyVertex, Collider coll, Transform collT, Vector3 velVec, float vel, Vector3 transformedColl)
        {
            CapsuleCollider capcoll = ((CapsuleCollider)coll);
            Vector3 localScale = collT.localScale;
            float height;
            float radius;
            Vector3 capsuleHeightDir;
            switch (capcoll.direction)
            {
                //todo radius has to be scaled correctly according to mesh scale, probably the same issue as with the box collider
                case 0:
                    height = ((capcoll.height / 2) * localScale.x / tlocalScale.x);
                    capsuleHeightDir = t.InverseTransformDirection(collT.right * height); // x
                    radius = capcoll.radius * ((localScale.y / tlocalScale.y + localScale.z / tlocalScale.z) / 2);
                    break;
                case 1:
                    height = ((capcoll.height / 2) * localScale.y / tlocalScale.y);
                    capsuleHeightDir = t.InverseTransformDirection(collT.up * height); // y
                    radius = capcoll.radius * ((localScale.x / tlocalScale.x + localScale.z / tlocalScale.z) / 2);
                    break;
                case 2:
                    height = ((capcoll.height / 2) * localScale.z / tlocalScale.z);
                    capsuleHeightDir = t.InverseTransformDirection(collT.forward * height); // z
                    radius = capcoll.radius * ((localScale.x / tlocalScale.x + localScale.y / tlocalScale.y) / 2);
                    break;
                default:
                    return false;
            }
            Vector3 capsuleHeightDirN = capsuleHeightDir.normalized;
            //get bounds into local space
            var tbounds = t.InverseTransformDirection(coll.bounds.extents * (1 + EffectRadius));
            var checksizeX = Mathf.Abs(tbounds.x / tlocalScale.x);
            float negCheckSizeX = transformedColl.x - checksizeX;
            checksizeX += transformedColl.x;

            var checksizeY = Mathf.Abs(tbounds.y / tlocalScale.y);
            float negCheckSizeY = transformedColl.y - checksizeY;
            checksizeY += transformedColl.y;

            var checksizeZ = Mathf.Abs(tbounds.z / tlocalScale.z);
            float negCheckSizeZ = transformedColl.z - checksizeZ;
            checksizeZ += transformedColl.z;

            var cylHeight = height - radius;
            height *= (1 + EffectRadius);
            var checkRadius = radius * (1 + EffectRadius);
            var cylHeightVec = cylHeight * capsuleHeightDirN;
            var negCylHeightVec = -cylHeightVec;

            var count = vertices.Count;
            for (int i = 0; i < count; i++)
            {

                var vert = vertices[i];
                if (vert.x > checksizeX || vert.x < negCheckSizeX)
                {
                    continue;
                }
                if (vert.y > checksizeY || vert.y < negCheckSizeY)
                {
                    continue;
                }
                if (vert.z > checksizeZ || vert.z < negCheckSizeZ)
                {
                    continue;
                }
                if (vertexOffsets[i].sqrMagnitude >= smaxIdent || (EffectRadius != 0 && jellyVertexOffsets[i].sqrMagnitude >= smaxIdent))
                {
                    touchtimes[i] = currentTime;
                    continue;
                }
                if (ShouldSkipVert(i))
                {
                    continue;
                }
                vert -= transformedColl;

                float heightProj = Vector3.Dot(vert, capsuleHeightDirN);
                //inside collider height
                if (heightProj <= height && heightProj >= -height)
                {
                    float dist;
                    if (cylHeight >= 0)
                    {
                        if (heightProj <= cylHeight && heightProj >= -cylHeight)
                        {
                            //were inside the cylinder part of the capsule
                            //check if dist to vert alongisde vec orthogonal to height is smaller than radius
                            //the mult is the closest point to vert on the height vector
                            dist = (vert - (heightProj * capsuleHeightDirN)).magnitude;
                        }
                        //were inside the half sphere part of the capsule,
                        //check what half, then calculate the dist and has to be smaller than radius
                        else
                        {
                            if (heightProj >= 0)
                            {
                                dist = (vert - cylHeightVec).magnitude;
                            }
                            else
                            {
                                dist = (vert - negCylHeightVec).magnitude;
                            }
                        }
                    }
                    else
                    {
                        //we have no cylinder part, just check if were in the sphere
                        dist = vert.magnitude;
                    }
                    if (dist < radius)
                    {
                        float scale = ((radius - dist) / radius) * sindentscale;
                        var move = ((vert * scale) + (velVec * dragscale));
                        if (isSkinned)
                        {
                            vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * move;
                        }
                        else
                        {
                            vertexOffsets[i] += move;
                        }
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                    }
                    else if (EffectRadius > 0
                    && dist > radius
                    && dist < checkRadius)
                    {
                        if (JellyEnabled
                            && vertexOffsets[i] == zero
                            && vel > VelocityThreshhold)
                        {
                            jellyVertexOffsets[i] = normals[i] * (-1 * (dist - checkRadius) * (sindentscale * sjellystrength * (1 + vel)));
                            jellyTouchTimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                        else if (DragEnabled)
                        {
                            var move = (velVec * (dragscale * ((checkRadius - dist) / (checkRadius - radius))));
                            if (isSkinned)
                            {
                                vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * move;
                            }
                            else
                            {
                                vertexOffsets[i] += move;
                            }
                            //fakeVolumeVertexOffsets[i] = zero;
                            touchtimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                    }
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ResetOffsets(bool setAnyVertex, float currentTime, int x)
        {
            var time = (currentTime - (touchtimes[x] + RecoveryDelayTime)) / IndentRecoveryTime;
            var jtime = (currentTime - (jellyTouchTimes[x] + jellyBuildup)) / JellyRecoveryTime;

            if (vertexOffsets[x].sqrMagnitude > 0.000001f)
            {
                touchCount++;
            }
            setAnyVertex |= time <= 1f;
            if (isSSE2Supported)
            {
                if (time >= 0)
                {
                    if (time < 1)
                    {
                        vertexOffsets[x] = FastLerp(vertexOffsets[x], zero, time);
                    }
                    else
                    {
                        vertexOffsets[x] = zero;
                    }
                }

                if (jtime >= 0 && jtime < 1)
                {
                    jellyVertexOffsets[x] = FastLerp(jellyVertexOffsets[x], zero, jtime);
                }
            }
            else
            {
                if (time >= 0)
                {
                    if (time < 1)
                    {
                        vertexOffsets[x] = Lerp(vertexOffsets[x], zero, time);
                    }
                    else
                    {
                        vertexOffsets[x] = zero;
                    }
                }

                if (jtime >= 0 && jtime < 1)
                {
                    jellyVertexOffsets[x] = Lerp(jellyVertexOffsets[x], zero, jtime);
                }
            }

            return setAnyVertex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void HandleFakeVolume(float currentTime)
        {
            var count = vertices.Count;
            if (isAvxSupported)
            {
                FastSumOffsets(count);
            }
            else
            {
                if (FixDuplicateVertices && identicalVerts is not null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (ShouldSkipVertImpl(i))
                        {
                            continue;
                        }
                        //this cahnges correctly
                        totalTouchValue += vertexOffsets[i];
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        //this cahnges correctly
                        totalTouchValue += vertexOffsets[i];
                    }
                }
            }

            if (touchCount != 0)
            {
                if (FixDuplicateVertices)
                {
                    changePerVert = (changePerVert + (Mathf.Abs(totalTouchValue.magnitude) / touchCount) / (vertices.Count - dupeVertCount - touchCount)) / 2;
                }
                else
                {
                    changePerVert = (changePerVert + (Mathf.Abs(totalTouchValue.magnitude) / touchCount) / (vertices.Count - touchCount)) / 2;
                }
            }
            else
            {
                changePerVert = 0;
            }

            float factor = (changePerVert * FakeVolumeScale * 512);

            for (int i = 0; i < count; i++)
            {
                if (ShouldSkipVert(i))
                {
                    continue;
                }
                Vector3 offs = vertexOffsets[i];
                if (offs.x <= 0.00000001f && offs.y <= 0.00000001f && offs.z <= 0.00000001f)
                {
                    fakeVolumeVertexOffsets[i] = normals[i] * factor;
                    //touchtimes[i] = currentTime;
                }
                else
                {
                    fakeVolumeVertexOffsets[i] = zero;
                }
            }
        }
    }

    [BurstCompile]
    private void FastSumOffsets(int count)
    {
        var surplus = count % 2;
        v256 sum = new(0);
        if (FixDuplicateVertices && identicalVerts is not null)
        {
            for (int i = 0; i < count - surplus; i += 2)
            {
                var v1 = ShouldSkipVertImpl(i) ? zero : vertexOffsets[i];
                var v2 = ShouldSkipVertImpl(i + 1) ? zero : vertexOffsets[i + 1];
                v256 vec = new(v1.x, v1.y, v1.z, 0, v2.x, v2.y, v2.z, 0);
                sum = X86.Avx.mm256_add_ps(sum, vec);
            }
        }
        else
        {
            for (int i = 0; i < count - surplus; i += 2)
            {
                var v1 = vertexOffsets[i];
                var v2 = vertexOffsets[i + 1];
                v256 vec = new(v1.x, v1.y, v1.z, 0, v2.x, v2.y, v2.z, 0);
                sum = X86.Avx.mm256_add_ps(sum, vec);
            }
        }
        totalTouchValue.x = sum.Float0 + sum.Float4;
        totalTouchValue.y = sum.Float1 + sum.Float5;
        totalTouchValue.z = sum.Float2 + sum.Float6;
        if (surplus > 0)
        {
            totalTouchValue += vertexOffsets[^1];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldSkipVert(int i)
    {
        if (FixDuplicateVertices && identicalVerts is not null)
        {
            //if this current vertex has a duplicate one that already appeared, we can skip it
            return ShouldSkipVertImpl(i);
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldSkipVertImpl(int i)
    {
        return identicalVerts[i, 0] < i && identicalVerts[i, 0] > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        Vector3 result = new()
        {
            x = a.x + (b.x - a.x) * t,
            y = a.y + (b.y - a.y) * t,
            z = a.z + (b.z - a.z) * t
        };
        return result;
    }

    [BurstCompile]
    public static Vector3 FastLerp(Vector3 a, Vector3 b, float t)
    {
        v128 tv = new(t);
        v128 av = new(a.x, a.y, a.z, 0);
        v128 bv = new(b.x, b.y, b.z, 0);
        v128 res = X86.Sse.add_ps(av, X86.Sse.mul_ps(tv, X86.Sse.sub_ps(bv, av)));
        return new Vector3()
        {
            x = res.Float0,
            y = res.Float1,
            z = res.Float2
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 OverLerp(Vector3 a, Vector3 b, float times, float t)
    {
        //the second part gives a praubla anchored at (0/1) and (1/1) with maximum wherever you set times + 1.
        var s = t * (1 + times - ((0.5f - t) * (0.5f - t) / (0.25f / times)));
        Vector3 result = new()
        {
            x = a.x + (b.x - a.x) * s,
            y = a.y + (b.y - a.y) * s,
            z = a.z + (b.z - a.z) * s
        };
        return result;
    }

    private void SetVertices()
    {
        float currentTime = Time.time;
        int len = 0;
        var count = vertices.Count;
        if (FixDuplicateVertices)
        {
            len = identicalVerts.GetLength(1);
        }

        if (FixDuplicateVertices && identicalVerts is not null)
        {
            for (int i = 0; i < count; i++)
            {
                //if this current vertex has a duplicate one that already appeared, we can skip it
                if (ShouldSkipVertImpl(i))
                {
                    continue;
                }
                for (int x = 0; x < len; x++)
                {
                    int index = identicalVerts[i, x];
                    if (index <= 0)
                    {
                        break;
                    }

                    SetVertsImpl(index, i);
                }
                SetVertsImpl(i, i);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                SetVertsImpl(i, i);
            }
        }
        Mesh.SetVertices(vertices);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SetVertsImpl(int index, int source)
        {
            var jtime = (currentTime - jellyTouchTimes[source]) / jellyBuildup;
            if (jtime >= 0 && jtime < 1)
            {
                vertices[index] = oldVertices[index] + vertexOffsets[source] + fakeVolumeVertexOffsets[source] + Lerp(zero, jellyVertexOffsets[source], jtime);
            }
            else
            {
                vertices[index] = oldVertices[index] + vertexOffsets[source] + fakeVolumeVertexOffsets[source] + jellyVertexOffsets[source];
            }
        }
    }

    private void GetVertices()
    {
        if (!isSkinned)
        {
            Mesh.GetVertices(vertices);
            Mesh.GetNormals(normals);
        }
        else
        {
            vertBuffer ??= ((SkinnedMeshRenderer)renderer).GetVertexBuffer();
            if (vertBuffer is not null)
            {
                if (vertices.Count == 0)
                {
                    verticesF = new ExampleVertex[vertBuffer.count];
                    vertBuffer.GetData(verticesF);
                    vertices.Capacity = vertBuffer.count;
                    normals.Capacity = vertBuffer.count;

                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        ExampleVertex vert = verticesF[i];
                        vertices.Add(vert.pos);
                        normals.Add(vert.normal);
                    }

                    int count = vertices.Count;
                    oldVertices = new Vector3[count];
                    oldNormals = new Vector3[count];
                    //touchNormals = new Vector3[vertices.Count];
                    Mesh.vertices.CopyTo(oldVertices, 0);
                    Mesh.normals.CopyTo(oldNormals, 0);
                    vertexOffsets = new Vector3[count];
                    fakeVolumeVertexOffsets = new Vector3[count];
                    jellyVertexOffsets = new Vector3[count];
                    touchtimes = new float[count];
                    jellyTouchTimes = new float[count];
                    if (!FixDuplicateVertices)
                    {
                        return;
                    }
                    SetUpMeshFixes();
                }
                else
                {
                    vertBuffer.GetData(verticesF);
                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        ExampleVertex vert = verticesF[i];
                        vertices[i] = vert.pos;
                        normals[i] = vert.normal;
                    }
                }
            }
        }
    }

    private void OnDestroy()
    {
        oldVertices = null;
        oldNormals = null;
        identicalVerts = null;
        vertices.Clear();
        normals.Clear();
        collNormals.Clear();
        collVerts.Clear();
        oldCollPos.Clear();
        vertexOffsets = null;
        jellyVertexOffsets = null;
        fakeVolumeVertexOffsets = null;
        touchtimes = null;
        jellyTouchTimes = null;
        verticesF = null;
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct ExampleVertex
{
    public Vector3 pos;
    public Vector3 normal;
    public Vector4 tangent;
}
