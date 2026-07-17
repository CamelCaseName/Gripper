using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using UnityEditor.Experimental.GraphView;
using UnityEngine;

public class SkinDeform : MonoBehaviour
{
    private const float jellyBuildup = 0.05f;
    private readonly List<Collider> colliders = new();
    private MeshFilter filter = null;
    private Mesh _mesh;
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
                        filter = GetComponent<MeshFilter>();
                    }
                    _mesh = filter.sharedMesh;
                }
            }
            return _mesh;
            throw new InvalidDataException("no MeshRenderer or SkinnedMeshRenderer present on the object");
        }
    }
    private bool isSkinned = false;
    private Vector3[] oldVertices;
    private int[,] identicalVerts;
    private Vector3[] oldNormals;
    //todo for gpu: use vertex shader and keep offsets and stuff on gopu, only supply collider data each frame

    //todo add option to preprocess meshcollider mesh for non-convex meshes to check the thickness behind each vertex -> max depth

    //todo add option to preprocess meshcollider mesh to find all nearby vertices and store
    //them in a list so we can calculate the sphere size needed to be close enough to the vertex
    private Vector3[] vertexOffsets;
    private Vector3[] fakeVolumeVertexOffsets;
    private Vector3[] jellyVertexOffsets;
    private readonly List<Vector3> vertices = new();
    private readonly List<Vector3> normals = new();
    private ExampleVertex[] verticesF;
    private float[] touchtimes;
    private float[] jellyTouchTimes;
    private GraphicsBuffer vertBuffer = null;
    //todo add tooltips, min max and stuff and shit here
    [Tooltip("Plug the objects renderer into here")]
    public Renderer renderer = null;

    [Tooltip("Colliders with this Tag will be respected for collision")]
    public string colliderTag = "SkinColliders";

    [Min(0)]
    [Tooltip("This defines how deep the mesh is deformed. 1 = whole length. Can be multiple")]
    public float MaxIndent = 0.03f;

    [Range(0, 1)]
    [Tooltip("This defines how fast the mesh is deformed. 1 = instant")]
    public float IndentScale = 0.7f;

    [Min(0)]
    [Tooltip("This defines how fast the mesh is reformed in seconds. 0 = instant")]
    public float RecoveryDelayTime = 0.1f;

    [Min(0)]
    [Tooltip("This defines how long to wait until the mesh is reformed, in seconds. 0 = instant")]
    public float IndentRecoveryTime = 2f;

    [Min(0)]
    [Tooltip("This defines how much further the jelly effect is triggered. 1 = colliders size doubled.")]
    public float JellyRadius = 0.15f;

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
    public float FakeVolumeScale = 0.025f;

    [Tooltip("If this is turned on, the objects mesh is scanned for duplicate vertices which will then be handled the same way -> no tears")]
    public bool FixDuplicateVertices = false;

    [Tooltip("If this is turned on, the ´collider also slightly drags the vertices on collision")]
    public bool DragEnabled = false;

    [Tooltip("This sets how strongly the collider drags the object")]
    public float DragScale = 0.025f;

    private readonly List<Vector3> oldCollPos = new();
    private readonly List<int> collType = new();
    private readonly Vector3 zero = Vector3.zero;
    private bool prevsetAnyVertex = false;
    private float changePerVert = 0;
    private int touchCount = 0;
    private Vector3 totalTouchValue = Vector3.zero;
    private int dupeVertCount = 0;
    private readonly float[] differences = new float[6];
    private readonly bool isSSE2Supported = X86.Sse2.IsSse2Supported;
    private readonly bool isAvxSupported = X86.Avx.IsAvxSupported;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private void Awake()
    {
        foreach (var go in GameObject.FindGameObjectsWithTag(colliderTag))
        {
            if (go.TryGetComponent<SphereCollider>(out var coll))
            {
                colliders.Add(coll);
                collType.Add(0);
                oldCollPos.Add(coll.transform.position);
            }
            if (go.TryGetComponent<BoxCollider>(out var box))
            {
                colliders.Add(box);
                collType.Add(1);
                oldCollPos.Add(box.transform.position);
            }
            if (go.TryGetComponent<MeshCollider>(out var m))
            {
                colliders.Add(m);
                collType.Add(2);
                oldCollPos.Add(m.transform.position);
            }
        }
        if (renderer is SkinnedMeshRenderer srender)
        {
            isSkinned = true;
        }
        GetVertices();
        oldVertices = new Vector3[vertices.Count];
        oldNormals = new Vector3[vertices.Count];
        //touchNormals = new Vector3[vertices.Count];
        vertices.CopyTo(oldVertices, 0);
        normals.CopyTo(oldNormals, 0);
        vertexOffsets = new Vector3[vertices.Count];
        jellyVertexOffsets = new Vector3[vertices.Count];
        fakeVolumeVertexOffsets = new Vector3[vertices.Count];
        touchtimes = new float[vertices.Count];
        jellyTouchTimes = new float[vertices.Count];
        if (!FixDuplicateVertices)
        {
            return;
        }
        SetUpMeshFixes();
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

    //todo change to computeshader on toggle so the user can choose between CPU and GPU load
    private void Update()
    {
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
        float sjelly = (1 + JellyRadius);
        var smaxIdent = MaxIndent / tlocalScale.magnitude;
        totalTouchValue = zero;
        touchCount = 0;
        var collCount = colliders.Count;
        for (int c = 0; c < collCount; c++)
        {
            Collider coll = colliders[c];
            if (!coll.bounds.Intersects(renderer.bounds)) //dont need scaled bounds here as the up wave can only start once we press into the obj
            {
                continue;
            }

            Transform collT = coll.transform;
            Vector3 velVec = collT.position - oldCollPos[c];
            var vel = velVec.magnitude;
            //move collider to local space
            var transformedColl = t.InverseTransformPoint(collT.position);

            //0 = sphere, 1 = cube, 2 = mesh
            int coltype = collType[c];
            if (coltype == 0)
            {
                HandleSphereCollider(ref setAnyVertex, coll, vel, transformedColl);
            }
            else if (coltype == 1)
            {
                HandleCubeColl(ref setAnyVertex, coll, collT, vel, transformedColl);
            }
            //todo add support for mesh
            //check if other vert is on the correct side with inverse normal of collider mesh, then check if close enought and move away
            else if (coltype == 2)
            {
                var collLocalScale = collT.localScale;

                var mcoll = coll as MeshCollider;
                List<Vector3> verts = new();
                var buffer = ((SkinnedMeshRenderer)renderer).GetVertexBuffer();
                if (buffer is not null)
                {
                    //if (vertices.Count == 0)
                    //{
                    //    verticesF = new ExampleVertex[buffer.count];
                    //    buffer.GetData(verticesF);
                    //    for (int i = 0; i < verticesF.Length; i++)
                    //    {
                    //        vertices.Add(verticesF[i].pos);
                    //    }

                    //    for (int i = 0; i < verticesF.Length; i++)
                    //    {
                    //        normals.Add(verticesF[i].normal);
                    //    }

                    //    oldVertices = new Vector3[vertices.Count];
                    //    oldNormals = new Vector3[vertices.Count];
                    //    //touchNormals = new Vector3[vertices.Count];
                    //    Mesh.vertices.CopyTo(oldVertices, 0);
                    //    Mesh.normals.CopyTo(oldNormals, 0);
                    //    vertexOffsets = new Vector3[vertices.Count];
                    //    vertexJellyOffsets = new Vector3[vertices.Count];
                    //    touchtimes = new float[vertices.Count];
                    //    vertexJellyTouched = new bool[vertices.Count];
                    //}
                    //else
                    //{
                    //    buffer.GetData(verticesF);
                    //    for (int i = 0; i < verticesF.Length; i++)
                    //    {
                    //        vertices[i] = verticesF[i].pos;
                    //    }

                    //    for (int i = 0; i < verticesF.Length; i++)
                    //    {
                    //        normals[i] = verticesF[i].normal;
                    //    }
                    //}
                }

                ////angle is finally correct when scaled.
                Matrix4x4 ltwtranspose = t.localToWorldMatrix.transpose;
                Vector3 collPos = collT.position;
                var vertNormal = ltwtranspose.MultiplyVector(collT.right);
                vertNormal.x *= tlocalScale.x;
                vertNormal.y *= tlocalScale.y;
                vertNormal.z *= tlocalScale.z;
                vertNormal = t.rotation * vertNormal;
                var vertP = t.InverseTransformPoint(collPos + vertNormal);

                //var cubeForwardN = sindentscale * (cubeForwardP - transformedColl).normalized;
                //var cubeBackN = -cubeForwardN;

                ////transform the local axes
                //var extents = coll.bounds.extents;
                //var axisX = t.InverseTransformVector(extents.x, 0, 0);
                //var axisY = t.InverseTransformVector(0, extents.y, 0);
                //var axisZ = t.InverseTransformVector(0, 0, extents.z);

                ////sum their absolute value to get the world extents
                //extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
                //extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
                //extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

                ////when the mesh is scaled not uniformly, the collider vector length is not correct
                //var count = vertices.Count;
                //for (int i = 0; i < count; i++)
                //{
                //if (!ShouldSkipVert(i))
                //{
                //    continue;
                //}
                //    var vert = vertices[i];

                //    if (transformedColl.x - extents.x >= vert.x || vert.x >= transformedColl.x + extents.x)
                //    {
                //        continue;
                //    }
                //    if (transformedColl.y - extents.y >= vert.y || vert.y >= transformedColl.y + extents.y)
                //    {
                //        continue;
                //    }
                //    if (transformedColl.z - extents.z >= vert.z || vert.z >= transformedColl.z + extents.z)
                //    {
                //        continue;
                //    }
                //    if (vertexOffsets[i].sqrMagnitude >= smaxIdent || JellyRadius != 0 && vertexJellyOffsets[i].sqrMagnitude >= smaxIdent)
                //    {
                //        continue;
                //    }
                //    //calculate closes face of our collider
                //    //point has to have 6x  to be inside of our collider
                //    var diffDown = Vector3.Dot(cubeDownN, vert - cubeDownP);
                //    if (diffDown < 0)
                //    {
                //        differences[0] = diffDown;
                //    }
                //    else
                //    {
                //        continue;
                //    }
                //    
                //        if (isSkinned)
                //        {
                //            vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (sindentscale * diffDown * cubeDownN);
                //        }
                //        else
                //        {
                //            vertexOffsets[i] -= (sindentscale * diffDown * cubeDownN);
                //        }
                //        touchtimes[i] = currentTime;
                //        setAnyVertex = true;
                //        indent = true;
                //        continue;
                //    
                //    //just with a bigger colliding box and only for a point that does not lie inside the real collider
                //    //else if (JellyRadius > 0
                //    //    && !vertexJellyTouched[i]
                //    //    && indent
                //    //    && vertexOffsets[i] == zero
                //    //    && vel > VelocityThreshhold
                //    //    && diffLength > (collSize)
                //    //    && diffLength < (checksize))
                //    //{
                //    //    vertexJellyOffsets[i] = -normals[i] * ((diffLength - (collSize + sjellyradius)) * (sindentscale * sjellystrength * (1 + vel - VelocityThreshhold)));
                //    //    touchtimes[i] = currentTime;
                //    //    vertexJellyTouched[i] = true;
                //    //    setAnyVertex = true;
                //    //}
            }
            oldCollPos[c] = coll.transform.position;
        }

        //if we have not set anything this and prev frame why bother
        if (!prevsetAnyVertex && !setAnyVertex)
        {
            return;
        }

        if (FixDuplicateVertices)
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
        bool SetVertOffset(Vector3 dir, int i, float jellydist)
        {
            if (jellydist <= 0 || sjelly == 1)
            {
                float scale = sindentscale * jellydist;
                if (isSkinned)
                {
                    vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (scale * dir);
                }
                else
                {
                    vertexOffsets[i] -= (scale * dir);
                }
                touchtimes[i] = currentTime;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool HandleSphereCollider(ref bool setAnyVertex, Collider coll, float vel, Vector3 transformedColl)
        {
            var tbounds = coll.bounds.extents;
            var checksizeX = (tbounds.x * (1 + JellyRadius) / tlocalScale.x);
            var checksizeY = (tbounds.y * (1 + JellyRadius) / tlocalScale.y);
            var checksizeZ = (tbounds.z * (1 + JellyRadius) / tlocalScale.z);
            var collSize = ((tbounds.x / tlocalScale.x) + (tbounds.y / tlocalScale.y) + (tbounds.z / tlocalScale.z)) / 3;
            var checksize = (checksizeX + checksizeY + checksizeZ) / 3;
            var count = vertices.Count;
            for (int i = 0; i < count; i++)
            {
                if (ShouldSkipVert(i))
                {
                    continue;
                }

                var vert = vertices[i] - transformedColl;
                if (vert.x > checksizeX || vert.x < -checksizeX)
                {
                    continue;
                }
                if (vert.y > checksizeY || vert.y < -checksizeY)
                {
                    continue;
                }
                if (vert.z > checksizeZ || vert.z < -checksizeZ)
                {
                    continue;
                }
                if (vertexOffsets[i].sqrMagnitude >= smaxIdent || JellyRadius != 0 && jellyVertexOffsets[i].sqrMagnitude >= smaxIdent)
                {
                    touchtimes[i] = currentTime;
                    continue;
                }

                var diffLength = vert.magnitude;
                if (diffLength < collSize)
                {
                    //todo add option for "smooshing/dragging" in the collider velocity direction
                    float scale = ((collSize - diffLength) / collSize) * sindentscale;
                    if (isSkinned)
                    {
                        vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * vert * scale;
                    }
                    else
                    {
                        vertexOffsets[i] += scale * vert;
                    }
                    touchtimes[i] = currentTime;
                    setAnyVertex = true;
                }
                else if (JellyRadius > 0
                    && vertexOffsets[i] == zero
                    && vel > VelocityThreshhold
                    && diffLength > collSize
                    && diffLength < checksize)
                {
                    jellyVertexOffsets[i] = normals[i] * (-1 * (diffLength - checksize) * (sindentscale * sjellystrength * (1 + vel)));
                    jellyTouchTimes[i] = currentTime;
                    setAnyVertex = true;
                }
            }

            return setAnyVertex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool HandleCubeColl(ref bool setAnyVertex, Collider coll, Transform collT, float vel, Vector3 transformedColl)
        {
            var collLocalScale = collT.localScale * sjelly;
            var extents = coll.bounds.extents * sjelly;
            var scaledSizeX = (collLocalScale.x / tlocalScale.x) * (JellyRadius) / 4;
            var scaledSizeY = (collLocalScale.y / tlocalScale.y) * (JellyRadius) / 4;
            var scaledSizeZ = (collLocalScale.z / tlocalScale.z) * (JellyRadius) / 4;

            //angle is finally correct when scaled.
            //todo collider becomes a little smaller the more the mesh is stretched
            Matrix4x4 ltwtranspose = t.localToWorldMatrix.transpose;
            Vector3 collPos = collT.position;
            var gcubeRight = ltwtranspose.MultiplyVector(collT.right);
            gcubeRight.x *= tlocalScale.x;
            gcubeRight.y *= tlocalScale.y;
            gcubeRight.z *= tlocalScale.z;
            gcubeRight = t.rotation * gcubeRight.normalized * (collLocalScale.x / 2);
            var cubeRightP = t.InverseTransformPoint(collPos + gcubeRight);
            var cubeLeftP = t.InverseTransformPoint(collPos - gcubeRight);

            var gcubeUp = ltwtranspose.MultiplyVector(collT.up);
            gcubeUp.x *= tlocalScale.x;
            gcubeUp.y *= tlocalScale.y;
            gcubeUp.z *= tlocalScale.z;
            gcubeUp = t.rotation * gcubeUp.normalized * (collLocalScale.y / 2);
            var cubeUpP = t.InverseTransformPoint(collPos + gcubeUp);
            var cubeDownP = t.InverseTransformPoint(collPos - gcubeUp);

            var gcubeForward = ltwtranspose.MultiplyVector(collT.forward);
            gcubeForward.x *= tlocalScale.x;
            gcubeForward.y *= tlocalScale.y;
            gcubeForward.z *= tlocalScale.z;
            gcubeForward = t.rotation * gcubeForward.normalized * (collLocalScale.z / 2);
            var cubeForwardP = t.InverseTransformPoint(collPos + gcubeForward);
            var cubeBackP = t.InverseTransformPoint(collPos - gcubeForward);

            var cubeForwardN = sindentscale * (cubeForwardP - transformedColl).normalized;
            var cubeBackN = -cubeForwardN;
            var cubeRightN = sindentscale * (cubeRightP - transformedColl).normalized;
            var cubeLeftN = -cubeRightN;
            var cubeUpN = sindentscale * (cubeUpP - transformedColl).normalized;
            var cubeDownN = -cubeUpN;

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
            for (int i = 0; i < count; i++)
            {
                if (ShouldSkipVert(i))
                {
                    continue;
                }
                var vert = vertices[i];

                if (transformedColl.x - extents.x >= vert.x || vert.x >= transformedColl.x + extents.x)
                {
                    continue;
                }
                if (transformedColl.y - extents.y >= vert.y || vert.y >= transformedColl.y + extents.y)
                {
                    continue;
                }
                if (transformedColl.z - extents.z >= vert.z || vert.z >= transformedColl.z + extents.z)
                {
                    continue;
                }
                if (vertexOffsets[i].sqrMagnitude >= smaxIdent || JellyRadius != 0 && jellyVertexOffsets[i].sqrMagnitude >= smaxIdent)
                {
                    continue;
                }
                //calculate closes face of our collider
                //point has to have 6x  to be inside of our collider
                var diffDown = Vector3.Dot(cubeDownN, vert - cubeDownP);
                if (diffDown < 0)
                {
                    differences[0] = diffDown;
                }
                else
                {
                    continue;
                }
                var diffForward = Vector3.Dot(cubeForwardN, vert - cubeForwardP);
                if (diffForward < 0)
                {
                    differences[1] = diffForward;
                }
                else
                {
                    continue;
                }
                var diffBack = Vector3.Dot(cubeBackN, vert - cubeBackP);
                if (diffBack < 0)
                {
                    differences[2] = diffBack;
                }
                else
                {
                    continue;
                }
                var diffRight = Vector3.Dot(cubeRightN, vert - cubeRightP);
                if (diffRight < 0)
                {
                    differences[3] = diffRight;
                }
                else
                {
                    continue;
                }
                var diffLeft = Vector3.Dot(cubeLeftN, vert - cubeLeftP);
                if (diffLeft < 0)
                {
                    differences[4] = diffLeft;
                }
                else
                {
                    continue;
                }
                var diffUp = Vector3.Dot(cubeUpN, vert - cubeUpP);
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
                //get the side corrected shove away vector
                if (min == diffDown)
                {
                    jelly = scaledSizeY + diffDown;
                    setAnyVertex = SetVertOffset(cubeDownN, i, jelly);
                }
                else if (min == diffForward)
                {
                    jelly = scaledSizeZ + diffForward;
                    setAnyVertex = SetVertOffset(cubeForwardN, i, jelly);
                }
                else if (min == diffBack)
                {
                    jelly = scaledSizeZ + diffBack;
                    setAnyVertex = SetVertOffset(cubeBackN, i, jelly);
                }
                else if (min == diffRight)
                {
                    jelly = scaledSizeX + diffRight;
                    setAnyVertex = SetVertOffset(cubeRightN, i, jelly);
                }
                else if (min == diffLeft)
                {
                    jelly = scaledSizeX + diffLeft;
                    setAnyVertex = SetVertOffset(cubeLeftN, i, jelly);
                }
                else if (min == diffUp)
                {
                    jelly = scaledSizeY + diffUp;
                    setAnyVertex = SetVertOffset(cubeUpN, i, jelly);
                }
                if (jelly > 0
                && vertexOffsets[i] == zero
                && vel > VelocityThreshhold)
                {
                    //this works and is fine
                    jellyVertexOffsets[i] = normals[i] * (jelly * (sindentscale * sjellystrength * (1 + vel)));
                    jellyTouchTimes[i] = currentTime;
                    setAnyVertex = true;
                }
                continue;
            }

            return setAnyVertex;
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
                if (time >= 0 && time < 1)
                {
                    vertexOffsets[x] = FastLerp(vertexOffsets[x], zero, time);
                }

                if (jtime >= 0 && jtime < 1)
                {
                    jellyVertexOffsets[x] = FastLerp(jellyVertexOffsets[x], zero, jtime);
                }
            }
            else
            {
                if (time >= 0 && time < 1)
                {
                    vertexOffsets[x] = Lerp(vertexOffsets[x], zero, time);
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
                for (int i = 0; i < count; i++)
                {
                    if (ShouldSkipVert(i))
                    {
                        continue;
                    }
                    //this cahnges correctly
                    totalTouchValue += vertexOffsets[i];
                }
            }

            touchCount += 1;
            if (FakeVolumeEnabled)
            {
                changePerVert = (changePerVert + (Mathf.Abs(totalTouchValue.magnitude) / touchCount) / (vertices.Count - dupeVertCount - touchCount)) / 2;
            }
            else
            {
                changePerVert = (changePerVert + (Mathf.Abs(totalTouchValue.magnitude) / touchCount) / (vertices.Count - touchCount)) / 2;
            }
            float factor = (changePerVert * FakeVolumeScale * 512);

            for (int i = 0; i < count; i++)
            {
                if (ShouldSkipVert(i))
                {
                    continue;
                }
                Vector3 offs = vertexOffsets[i];
                if (offs.x == 0 && offs.y == 0 && offs.z == 0)
                {
                    fakeVolumeVertexOffsets[i] = normals[i] * factor;
                    touchtimes[i] = currentTime;
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

        if (FixDuplicateVertices)
        {
            for (int i = 0; i < count; i++)
            {
                //if this current vertex has a duplicate one that already appeared, we can skip it
                if (ShouldSkipVert(i))
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
