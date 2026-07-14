using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

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
    bool isSkinned = false;
    private Vector3[] oldVertices;
    //private Vector3[] touchNormals;
    private Vector3[] oldNormals;
    //todo add list with vertex indices whose pos is identical so we move them by the same amount and dont rip the mesh apart

    //todo for gpu: use vertex shader and keep offsets and stuff on gopu, only supply collider data each frame

    //todo add option to preprocess meshcollider mesh for non-convex meshes to check the thickness behind each vertex -> max depth

    //todo add option to preprocess meshcollider mesh to find all nearby vertices and store
    //them in a list so we can calculate the sphere size needed to be close enough to the vertex
    private Vector3[] vertexOffsets;
    private Vector3[] vertexJellyOffsets;
    private readonly List<Vector3> vertices = new();
    private readonly List<Vector3> normals = new();
    ExampleVertex[] verticesF;
    private float[] touchtimes;
    private float[] jellyTouchtimes;
    //todo add tooltips, min max and stuff and shit here
    public Renderer renderer = null;
    public readonly string colliderTag = "SkinColliders";
    public float VelocityThreshhold = 0.03f;
    public float IndentScale = 0.7f;
    public float MaxIndent = 0.03f;
    public float IndentRecoveryTime = 2f;
    public float RecoveryDelayTime = 0.1f;
    public float JellyRadius = 0.15f;
    public float JellyRecoveryTime = 0.7f;
    public float JellyStrength = 1.1f;
    public bool GlobalJellyEnabled = false;
    public float GlobalJellyScale = 0.025f;
    readonly List<Vector3> oldCollPos = new();
    readonly List<int> collType = new();
    readonly Vector3 zero = Vector3.zero;
    bool prevsetAnyVertex = false;
    int touchCount = 0;
    float totalTouchValue = 0;
    readonly float[] differences = new float[6];
    //Bounds scaledBounds;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
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
        vertexJellyOffsets = new Vector3[vertices.Count];
        touchtimes = new float[vertices.Count];
        jellyTouchtimes = new float[vertices.Count];
    }

    //todo change to computeshader on toggle so the user can choose between CPU and GPU load
    void Update()
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
        var smaxIdent = MaxIndent / tlocalScale.magnitude;

        touchCount = 0;
        totalTouchValue = 0;
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

            //0 = sphere, 1 = cupe
            int coltype = collType[c];
            //todo vectorize the vertex for loops for each collider
            if (coltype == 0)
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
                    if (vertexOffsets[i].sqrMagnitude >= smaxIdent || JellyRadius != 0 && vertexJellyOffsets[i].sqrMagnitude >= smaxIdent)
                    {
                        touchtimes[i] = currentTime;
                        continue;
                    }

                    var diffLength = vert.magnitude;
                    if (diffLength < collSize)
                    {
                        //todo add option for "smooshing" in the collider velocity direction
                        float scale = ((collSize - diffLength) / collSize) * sindentscale;
                        if (isSkinned)
                        {
                            vertexOffsets[i] += Quaternion.FromToRotation(normals[i], oldNormals[i]) * vert * scale;
                        }
                        else
                        {
                            vertexOffsets[i] += scale * vert;
                        }
                        touchCount++;
                        totalTouchValue += scale;
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                    }
                    else if (JellyRadius > 0
                        && vertexOffsets[i] == zero
                        && vel > VelocityThreshhold
                        && diffLength > collSize
                        && diffLength < checksize)
                    {
                        vertexJellyOffsets[i] = normals[i] * (-1 * (diffLength - checksize) * (sindentscale * sjellystrength * (1 + vel)));
                        jellyTouchtimes[i] = currentTime;
                        setAnyVertex = true;
                    }
                }
            }
            else if (coltype == 1)
            {
                float sjelly = (1 + JellyRadius);
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
                    if (vertexOffsets[i].sqrMagnitude >= smaxIdent || JellyRadius != 0 && vertexJellyOffsets[i].sqrMagnitude >= smaxIdent)
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
                        if (jelly <= 0 || sjelly == 1)
                        {
                            float scale = sindentscale * jelly;
                            if (isSkinned)
                            {
                                vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (scale * cubeDownN);
                            }
                            else
                            {
                                vertexOffsets[i] -= (scale * cubeDownN);
                            }
                            touchCount++;
                            totalTouchValue += scale;
                            touchtimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                    }
                    else if (min == diffForward)
                    {
                        jelly = scaledSizeZ + diffForward;
                        if (jelly <= 0 || sjelly == 1)
                        {
                            float scale = sindentscale * diffForward;
                            if (isSkinned)
                            {
                                vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (scale * cubeForwardN);
                            }
                            else
                            {
                                vertexOffsets[i] -= (scale * cubeForwardN);
                            }
                            touchCount++;
                            totalTouchValue += scale;
                            touchtimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                    }
                    else if (min == diffBack)
                    {
                        jelly = scaledSizeZ + diffBack;
                        if (jelly <= 0 || sjelly == 1)
                        {
                            float scale = sindentscale * diffBack;
                            if (isSkinned)
                            {
                                vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (scale * cubeBackN);
                            }
                            else
                            {
                                vertexOffsets[i] -= (scale * cubeBackN);
                            }
                            touchCount++;
                            totalTouchValue += scale;
                            touchtimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                    }
                    else if (min == diffRight)
                    {
                        jelly = scaledSizeX + diffRight;
                        if (jelly <= 0 || sjelly == 1)
                        {
                            float scale = sindentscale * diffRight;
                            if (isSkinned)
                            {
                                vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (scale * cubeRightN);
                            }
                            else
                            {
                                vertexOffsets[i] -= (scale * cubeRightN);
                            }
                            touchCount++;
                            totalTouchValue += scale;
                            touchtimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                    }
                    else if (min == diffLeft)
                    {
                        jelly = scaledSizeX + diffLeft;
                        if (jelly <= 0 || sjelly == 1)
                        {
                            float scale = sindentscale * diffLeft;
                            if (isSkinned)
                            {
                                vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (scale * cubeLeftN);
                            }
                            else
                            {
                                vertexOffsets[i] -= (scale * cubeLeftN);
                            }
                            touchCount++;
                            totalTouchValue += scale;
                            touchtimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                    }
                    else if (min == diffUp)
                    {
                        jelly = scaledSizeY + diffUp;
                        if (jelly <= 0 || sjelly == 1)
                        {
                            float scale = sindentscale * diffUp;
                            if (isSkinned)
                            {
                                vertexOffsets[i] -= Quaternion.FromToRotation(normals[i], oldNormals[i]) * (scale * cubeUpN);
                            }
                            else
                            {
                                vertexOffsets[i] -= (scale * cubeUpN);
                            }
                            touchCount++;
                            totalTouchValue += scale;
                            touchtimes[i] = currentTime;
                            setAnyVertex = true;
                        }
                    }
                    if (jelly > 0
                    && vertexOffsets[i] == zero
                    && vel > VelocityThreshhold)
                    {
                        //this works and is fine
                        vertexJellyOffsets[i] = normals[i] * (jelly * (sindentscale * sjellystrength * (1 + vel)));
                        jellyTouchtimes[i] = currentTime;
                        setAnyVertex = true;
                    }
                    continue;
                }
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
                    //    //todo vectorize
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

        float changePerVert = Mathf.Abs(totalTouchValue) / (vertices.Count - touchCount);

        //todo vectorize
        for (int x = 0; x < vertexOffsets.Length; x++)
        {
            var time = (currentTime - (touchtimes[x] + RecoveryDelayTime)) / IndentRecoveryTime;
            var jtime = (currentTime - (jellyTouchtimes[x] + jellyBuildup)) / JellyRecoveryTime;

            setAnyVertex |= time <= 1f;
            if (time >= 0 && time < 1)
            {
                //todo tune value, then copy to jelly effect and dont overlerp here, or maybe depending on toggle and only little
                //for overlerping that is
                vertexOffsets[x] = Lerp(vertexOffsets[x], zero, time);
            }
            if (GlobalJellyEnabled && changePerVert > 0)
            {
                vertexJellyOffsets[x] += normals[x] * (changePerVert * GlobalJellyScale);
                jellyTouchtimes[x] = currentTime;
            }

            if (jtime >= 0 && jtime < 1)
            {
                vertexJellyOffsets[x] = Lerp(vertexJellyOffsets[x], zero, jtime);
            }
        }
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
        //todo vectorize
        var count = vertices.Count;
        for (int i = 0; i < count; i++)
        {
            var jtime = (currentTime - (jellyTouchtimes[i])) / jellyBuildup;
            if (jtime >= 0 && jtime < 1)
            {
                vertices[i] = oldVertices[i] + vertexOffsets[i] + Lerp(zero, vertexJellyOffsets[i], jtime);
            }
            else
            {
                vertices[i] = oldVertices[i] + vertexOffsets[i] + vertexJellyOffsets[i];
            }
        }
        Mesh.SetVertices(vertices);
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
            var buffer = ((SkinnedMeshRenderer)renderer).GetVertexBuffer();
            if (buffer is not null)
            {
                if (vertices.Count == 0)
                {
                    verticesF = new ExampleVertex[buffer.count];
                    buffer.GetData(verticesF);
                    //todo vectorize
                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        vertices.Add(verticesF[i].pos);
                    }

                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        normals.Add(verticesF[i].normal);
                    }

                    oldVertices = new Vector3[vertices.Count];
                    oldNormals = new Vector3[vertices.Count];
                    //touchNormals = new Vector3[vertices.Count];
                    Mesh.vertices.CopyTo(oldVertices, 0);
                    Mesh.normals.CopyTo(oldNormals, 0);
                    vertexOffsets = new Vector3[vertices.Count];
                    vertexJellyOffsets = new Vector3[vertices.Count];
                    touchtimes = new float[vertices.Count];
                }
                else
                {
                    buffer.GetData(verticesF);
                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        vertices[i] = verticesF[i].pos;
                    }

                    for (int i = 0; i < verticesF.Length; i++)
                    {
                        normals[i] = verticesF[i].normal;
                    }
                }
            }
        }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct ExampleVertex
{
    public Vector3 pos;
    public Vector3 normal;
    public Vector4 tangent;
}
