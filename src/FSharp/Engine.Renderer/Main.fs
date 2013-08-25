﻿(*
Copyright (C) 2013 William F. Smith

This program is free software; you can redistribute it
and/or modify it under the terms of the GNU General Public License as
published by the Free Software Foundation; either version 2 of the License,
or (at your option) any later version.

This program is distributed in the hope that it will be
useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA

Derivative of Quake III Arena source:
Copyright (C) 1999-2005 Id Software, Inc.
*)

namespace Engine.Renderer

// Disable native interop warnings
#nowarn "9"
#nowarn "51"

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Diagnostics
open System.Diagnostics.Contracts
open Microsoft.FSharp.NativeInterop
open Engine.Core
open Engine.Math
open Engine.NativeInterop

module Main =
    let flipMatrix =
        // convert from our coordinate system (looking down X)
        // to OpenGL's coordinate system (looking down -Z)
        Matrix16 (
            0.f, 0.f, -1.f, 0.f,
            -1.f, 0.f, 0.f, 0.f,
            0.f, 1.f, 0.f, 0.f,
            0.f, 0.f, 0.f, 1.f
        )

    module private LocalBox =
        /// <summary>
        /// Transform into world space.
        /// </summary>
        [<Pure>]
        let transformWorldSpace (bounds: Bounds) (orientation: OrientationR) =        
            Transform.init (fun i ->
                let v = Vector3 (bounds.[i &&& 1].X, bounds.[(i >>> 1) &&& 1].Y, bounds.[(i >>> 2) &&& 1].Z)

                orientation.Origin
                |> ( *+ ) (v.X, orientation.Axis.[0])
                |> ( *+ ) (v.Y, orientation.Axis.[1])
                |> ( *+ ) (v.Z, orientation.Axis.[2])
            )

        /// <summary>
        /// Check against frustum planes.
        /// </summary>
        [<Pure>]
        let checkFrustumPlanes (transformed: Transform) (frustum: Frustum) =
            let rec checkFrustumPlane (frust: Plane) front back isFront acc =
                match acc = 8 || isFront with
                | true -> (front, back)
                | _ ->
                    let distance = Vector3.dot transformed.[acc] frust.Normal

                    match distance > frust.Distance with
                    | true -> checkFrustumPlane frust 1 back (back = 1) (acc + 1)
                    | _ -> checkFrustumPlane frust front 1 false (acc + 1)

            let rec checkFrustumPlanes anyBack isFront acc =
                match acc = 4 || isFront = false with
                | true -> (anyBack, isFront)
                | _ ->
                    let frust = frustum.[acc]

                    match checkFrustumPlane frust 0 0 false 0 with
                    | (front, back) ->
                        checkFrustumPlanes (anyBack ||| back) (front = 1) (acc + 1)

            match checkFrustumPlanes 0 true 0 with
            | (_, false) -> ClipType.Out // all points were behind one of the planes
            | (0, _) -> ClipType.In // completely inside frustum
            | _ -> ClipType.Clip // partially clipped

    module private PointAndRadius =
        /// <summary>
        /// CheckFrustumPlanes
        /// </summary>
        [<Pure>]
        let checkFrustumPlanes (point: Vector3) (radius: single) (frustum: Frustum) =
            let rec checkFrustumPlanes mightBeClipped canCullOut acc =
                match acc = 4 || canCullOut with
                | true -> (mightBeClipped, canCullOut)
                | _ ->
                    let frust = frustum.[acc]
                    let distance = (Vector3.dot point frust.Normal) - frust.Distance

                    match distance < -radius with
                    | true -> checkFrustumPlanes mightBeClipped true (acc + 1)
                    | _ when distance <= radius -> checkFrustumPlanes true false (acc + 1)
                    | _ -> checkFrustumPlanes mightBeClipped false (acc + 1)

            match checkFrustumPlanes false false 0 with
            | (_, true) -> ClipType.Out // all points were behind one of the planes
            | (true, _) -> ClipType.Clip // partially clipped
            | _ -> ClipType.In // completely inside frustum


(*
int R_CullLocalBox (vec3_t bounds[2]) {
	int		i, j;
	vec3_t	transformed[8];
	float	dists[8];
	vec3_t	v;
	cplane_t	*frust;
	int			anyBack;
	int			front, back;

	if ( r_nocull->integer ) {
		return CULL_CLIP;
	}

	// transform into world space
	for (i = 0 ; i < 8 ; i++) {
		v[0] = bounds[i&1][0];
		v[1] = bounds[(i>>1)&1][1];
		v[2] = bounds[(i>>2)&1][2];

		VectorCopy( tr.or.origin, transformed[i] );
		VectorMA( transformed[i], v[0], tr.or.axis[0], transformed[i] );
		VectorMA( transformed[i], v[1], tr.or.axis[1], transformed[i] );
		VectorMA( transformed[i], v[2], tr.or.axis[2], transformed[i] );
	}

	// check against frustum planes
	anyBack = 0;
	for (i = 0 ; i < 4 ; i++) {
		frust = &tr.viewParms.frustum[i];

		front = back = 0;
		for (j = 0 ; j < 8 ; j++) {
			dists[j] = DotProduct(transformed[j], frust->normal);
			if ( dists[j] > frust->dist ) {
				front = 1;
				if ( back ) {
					break;		// a point is in front
				}
			} else {
				back = 1;
			}
		}
		if ( !front ) {
			// all points were behind one of the planes
			return CULL_OUT;
		}
		anyBack |= back;
	}

	if ( !anyBack ) {
		return CULL_IN;		// completely inside frustum
	}

	return CULL_CLIP;		// partially clipped
}
*)

    /// <summary>
    /// Based on Q3: R_CullLocalBox
    /// CullLocalBox
    // </summary>
    [<Pure>]
    let cullLocalBox (bounds: Bounds) (orientation: OrientationR) (frustum: Frustum) (noCull: Cvar) =
        match noCull.Integer = 1 with
        | true -> ClipType.Clip
        | _ ->

        // transform into world space
        let transformed = LocalBox.transformWorldSpace bounds orientation

        // check against frustum planes
        LocalBox.checkFrustumPlanes transformed frustum

(*
int R_CullPointAndRadius( vec3_t pt, float radius )
{
	int		i;
	float	dist;
	cplane_t	*frust;
	qboolean mightBeClipped = qfalse;

	if ( r_nocull->integer ) {
		return CULL_CLIP;
	}

	// check against frustum planes
	for (i = 0 ; i < 4 ; i++) 
	{
		frust = &tr.viewParms.frustum[i];

		dist = DotProduct( pt, frust->normal) - frust->dist;
		if ( dist < -radius )
		{
			return CULL_OUT;
		}
		else if ( dist <= radius ) 
		{
			mightBeClipped = qtrue;
		}
	}

	if ( mightBeClipped )
	{
		return CULL_CLIP;
	}

	return CULL_IN;		// completely inside frustum
}
*)

    /// <summary>
    /// Based on Q3: R_CullPointAndRadius
    /// CullPointAndRadius
    /// </summary>
    [<Pure>]
    let cullPointAndRadius (point: Vector3) (radius: single) (frustum: Frustum) (noCull: Cvar) =
        match noCull.Integer = 1 with
        | true -> ClipType.Clip
        | _ ->

        PointAndRadius.checkFrustumPlanes point radius frustum

(*
void R_LocalPointToWorld (vec3_t local, vec3_t world) {
	world[0] = local[0] * tr.or.axis[0][0] + local[1] * tr.or.axis[1][0] + local[2] * tr.or.axis[2][0] + tr.or.origin[0];
	world[1] = local[0] * tr.or.axis[0][1] + local[1] * tr.or.axis[1][1] + local[2] * tr.or.axis[2][1] + tr.or.origin[1];
	world[2] = local[0] * tr.or.axis[0][2] + local[1] * tr.or.axis[1][2] + local[2] * tr.or.axis[2][2] + tr.or.origin[2];
}
*)

    /// <summary>
    /// Based on Q3: R_LocalPointToWorld
    /// LocalPointToWorld
    /// </summary>
    [<Pure>]
    let localPointToWorld (local: Vector3) (orientation: OrientationR) =
        Vector3 (
            (local.X * orientation.Axis.[0].X) + (local.Y * orientation.Axis.[1].X) + (local.Z * orientation.Axis.[2].X) + orientation.Origin.X,
            (local.X * orientation.Axis.[0].Y) + (local.Y * orientation.Axis.[1].Y) + (local.Z * orientation.Axis.[2].Y) + orientation.Origin.Y,
            (local.X * orientation.Axis.[0].Z) + (local.Y * orientation.Axis.[1].Z) + (local.Z * orientation.Axis.[2].Z) + orientation.Origin.Z
        )

(*
void R_LocalNormalToWorld (vec3_t local, vec3_t world) {
	world[0] = local[0] * tr.or.axis[0][0] + local[1] * tr.or.axis[1][0] + local[2] * tr.or.axis[2][0];
	world[1] = local[0] * tr.or.axis[0][1] + local[1] * tr.or.axis[1][1] + local[2] * tr.or.axis[2][1];
	world[2] = local[0] * tr.or.axis[0][2] + local[1] * tr.or.axis[1][2] + local[2] * tr.or.axis[2][2];
}
*)

    /// <summary>
    /// Based on Q3: R_LocalNormalToWorld
    /// LocalNormalToWorld
    /// </summary>
    [<Pure>]
    let localNormalToWorld (local: Vector3) (orientation: OrientationR) =
        Vector3 (
            (local.X * orientation.Axis.[0].X) + (local.Y * orientation.Axis.[1].X) + (local.Z * orientation.Axis.[2].X),
            (local.X * orientation.Axis.[0].Y) + (local.Y * orientation.Axis.[1].Y) + (local.Z * orientation.Axis.[2].Y),
            (local.X * orientation.Axis.[0].Z) + (local.Y * orientation.Axis.[1].Z) + (local.Z * orientation.Axis.[2].Z)
        )

(*
void R_WorldToLocal (vec3_t world, vec3_t local) {
	local[0] = DotProduct(world, tr.or.axis[0]);
	local[1] = DotProduct(world, tr.or.axis[1]);
	local[2] = DotProduct(world, tr.or.axis[2]);
}
*)

    /// <summary>
    /// Based on Q3: R_WorldToLocal
    /// WorldToLocal
    /// </summary>
    [<Pure>]
    let worldToLocal (world: Vector3) (orientation: OrientationR) =
        Vector3 (
            Vector3.dot world orientation.Axis.[0],
            Vector3.dot world orientation.Axis.[1],
            Vector3.dot world orientation.Axis.[2]
        )


(*
int R_CullLocalPointAndRadius( vec3_t pt, float radius )
{
	vec3_t transformed;

	R_LocalPointToWorld( pt, transformed );

	return R_CullPointAndRadius( transformed, radius );
}
*)

    /// <summary>
    /// Based on Q3: R_CullLocalPointAndRadius
    /// CullLocalPointAndRadius
    /// </summary>
    [<Pure>]
    let cullLocalPointAndRadius (point: Vector3) (radius: single) (orientation: OrientationR) (frustum: Frustum) (noCull: Cvar) =
        let transformed = localPointToWorld point orientation
        cullPointAndRadius transformed radius frustum noCull

(*
void R_TransformModelToClip( const vec3_t src, const float *modelMatrix, const float *projectionMatrix,
							vec4_t eye, vec4_t dst ) {
	int i;

	for ( i = 0 ; i < 4 ; i++ ) {
		eye[i] = 
			src[0] * modelMatrix[ i + 0 * 4 ] +
			src[1] * modelMatrix[ i + 1 * 4 ] +
			src[2] * modelMatrix[ i + 2 * 4 ] +
			1 * modelMatrix[ i + 3 * 4 ];
	}

	for ( i = 0 ; i < 4 ; i++ ) {
		dst[i] = 
			eye[0] * projectionMatrix[ i + 0 * 4 ] +
			eye[1] * projectionMatrix[ i + 1 * 4 ] +
			eye[2] * projectionMatrix[ i + 2 * 4 ] +
			eye[3] * projectionMatrix[ i + 3 * 4 ];
	}
}
*)

    /// <summary>
    /// Based on Q3: R_CullLocalPointAndRadius
    /// TransformModelToClip
    /// </summary>
    [<Pure>]
    let transformModelToClip (source: Vector3) (modelMatrix: Matrix16) (projectionMatrix: Matrix16) =
        let calculateEye i =
            (source.X * modelMatrix.[0, i]) +
            (source.Y * modelMatrix.[1, i]) +
            (source.Z * modelMatrix.[2, i]) +
            (1.f * modelMatrix.[3, i])
          
        let eye =
            Vector4 (
                calculateEye 0,
                calculateEye 1,
                calculateEye 2,
                calculateEye 3
            )

        let calculateDestination i =
            (eye.X * projectionMatrix.[0, i]) +
            (eye.Y * projectionMatrix.[1, i]) +
            (eye.Z * projectionMatrix.[2, i]) +
            (eye.W * projectionMatrix.[3, i])

        let destination =
            Vector4 (
                calculateDestination 0,
                calculateDestination 1,
                calculateDestination 2,
                calculateDestination 3
            )

        (eye, destination)

(*
void R_TransformClipToWindow( const vec4_t clip, const viewParms_t *view, vec4_t normalized, vec4_t window ) {
	normalized[0] = clip[0] / clip[3];
	normalized[1] = clip[1] / clip[3];
	normalized[2] = ( clip[2] + clip[3] ) / ( 2 * clip[3] );

	window[0] = 0.5f * ( 1.0f + normalized[0] ) * view->viewportWidth;
	window[1] = 0.5f * ( 1.0f + normalized[1] ) * view->viewportHeight;
	window[2] = normalized[2];

	window[0] = (int) ( window[0] + 0.5 );
	window[1] = (int) ( window[1] + 0.5 );
}
*)
    
    /// <summary>
    /// Based on Q3: R_TransformClipToWindow
    /// TransformClipToWindow
    /// </summary>
    [<Pure>]
    let transformClipToWindow (clip: Vector4) (view: ViewParms) =
        let normalized =
            Vector4 (
                clip.X / clip.W,
                clip.Y / clip.W,
                (clip.Z + clip.W) / (2.f * clip.W),
                0.f
            )

        let window =
            Vector4 (
                truncate ((0.5f * (1.0f + normalized.X) * (single view.ViewportWidth)) + 0.5f),
                truncate ((0.5f * (1.0f + normalized.Y) * (single view.ViewportHeight)) + 0.5f),
                normalized.Z,
                0.f
            )

        (normalized, window)

(*
void myGlMultMatrix( const float *a, const float *b, float *out ) {
	int		i, j;

	for ( i = 0 ; i < 4 ; i++ ) {
		for ( j = 0 ; j < 4 ; j++ ) {
			out[ i * 4 + j ] =
				a [ i * 4 + 0 ] * b [ 0 * 4 + j ]
				+ a [ i * 4 + 1 ] * b [ 1 * 4 + j ]
				+ a [ i * 4 + 2 ] * b [ 2 * 4 + j ]
				+ a [ i * 4 + 3 ] * b [ 3 * 4 + j ];
		}
	}
}
*)

    // TODO: This will need to go away eventually.
    let myGLMultMatrix (a: Matrix16) (b: Matrix16) =
        a * b

(*
void R_RotateForEntity( const trRefEntity_t *ent, const viewParms_t *viewParms,
					   orientationr_t *or ) {
	float	glMatrix[16];
	vec3_t	delta;
	float	axisLength;

	if ( ent->e.reType != RT_MODEL ) {
		*or = viewParms->world;
		return;
	}

	VectorCopy( ent->e.origin, or->origin );

	VectorCopy( ent->e.axis[0], or->axis[0] );
	VectorCopy( ent->e.axis[1], or->axis[1] );
	VectorCopy( ent->e.axis[2], or->axis[2] );

	glMatrix[0] = or->axis[0][0];
	glMatrix[4] = or->axis[1][0];
	glMatrix[8] = or->axis[2][0];
	glMatrix[12] = or->origin[0];

	glMatrix[1] = or->axis[0][1];
	glMatrix[5] = or->axis[1][1];
	glMatrix[9] = or->axis[2][1];
	glMatrix[13] = or->origin[1];

	glMatrix[2] = or->axis[0][2];
	glMatrix[6] = or->axis[1][2];
	glMatrix[10] = or->axis[2][2];
	glMatrix[14] = or->origin[2];

	glMatrix[3] = 0;
	glMatrix[7] = 0;
	glMatrix[11] = 0;
	glMatrix[15] = 1;

	myGlMultMatrix( glMatrix, viewParms->world.modelMatrix, or->modelMatrix );

	// calculate the viewer origin in the model's space
	// needed for fog, specular, and environment mapping
	VectorSubtract( viewParms->or.origin, or->origin, delta );

	// compensate for scale in the axes if necessary
	if ( ent->e.nonNormalizedAxes ) {
		axisLength = VectorLength( ent->e.axis[0] );
		if ( !axisLength ) {
			axisLength = 0;
		} else {
			axisLength = 1.0f / axisLength;
		}
	} else {
		axisLength = 1.0f;
	}

	or->viewOrigin[0] = DotProduct( delta, or->axis[0] ) * axisLength;
	or->viewOrigin[1] = DotProduct( delta, or->axis[1] ) * axisLength;
	or->viewOrigin[2] = DotProduct( delta, or->axis[2] ) * axisLength;
}
*)

    /// <summary>
    /// Based on Q3: R_RotateForEntity
    /// RotateForEntity
    ///
    /// Generates an orientation for an entity and viewParms
    /// Does NOT produce any GL calls
    /// Called by both the front end and the back end
    /// </summary>
    [<Pure>]
    let rotateForEntity (entity: TrRefEntity) (viewParms: ViewParms) (orientation: OrientationR) =
        match entity.Entity.Type <> RefEntityType.Model with
        | true -> viewParms.World
        | _ ->

        let orientation =
            OrientationR (
                entity.Entity.Origin,
                entity.Entity.Axis,
                orientation.ViewOrigin,
                orientation.ModelMatrix
            )

        let glMatrix =
            Matrix16 (
                orientation.Axis.[0].[0],
                orientation.Axis.[0].[1],
                orientation.Axis.[0].[2],
                0.f,
                orientation.Axis.[1].[0],
                orientation.Axis.[1].[1],
                orientation.Axis.[1].[2],
                0.f,
                orientation.Axis.[2].[0],
                orientation.Axis.[2].[1],
                orientation.Axis.[2].[2],
                0.f,
                orientation.Origin.X,
                orientation.Origin.Y,
                orientation.Origin.Z,
                1.f
            )

        let orientation =
            OrientationR (
                orientation.Origin,
                orientation.Axis,
                orientation.ViewOrigin,
                glMatrix * viewParms.World.ModelMatrix
            )

        // calculate the viewer origin in the model's space
        // needed for fog, specular, and environment mapping
        let delta = viewParms.Orientation.Origin - orientation.Origin

        // compensate for scale in the axes if necessary
        let axisLength =
            match entity.Entity.HasNonNormalizedAxes with
            | true ->
                // Is it ok to compare the single like this?
                match Vector3.length entity.Entity.Axis.X with
                | 0.f -> 0.f
                | axisLength ->
                    1.0f / axisLength
            | _ -> 1.0f

        OrientationR (
            orientation.Origin,
            orientation.Axis,
            Vector3 (
                (Vector3.dot delta orientation.Axis.X) * axisLength,
                (Vector3.dot delta orientation.Axis.Y) * axisLength,
                (Vector3.dot delta orientation.Axis.Z) * axisLength
            ),
            orientation.ModelMatrix
        )

(*
void R_RotateForViewer (void) 
{
	float	viewerMatrix[16];
	vec3_t	origin;

	Com_Memset (&tr.or, 0, sizeof(tr.or));
	tr.or.axis[0][0] = 1;
	tr.or.axis[1][1] = 1;
	tr.or.axis[2][2] = 1;
	VectorCopy (tr.viewParms.or.origin, tr.or.viewOrigin);

	// transform by the camera placement
	VectorCopy( tr.viewParms.or.origin, origin );

	viewerMatrix[0] = tr.viewParms.or.axis[0][0];
	viewerMatrix[4] = tr.viewParms.or.axis[0][1];
	viewerMatrix[8] = tr.viewParms.or.axis[0][2];
	viewerMatrix[12] = -origin[0] * viewerMatrix[0] + -origin[1] * viewerMatrix[4] + -origin[2] * viewerMatrix[8];

	viewerMatrix[1] = tr.viewParms.or.axis[1][0];
	viewerMatrix[5] = tr.viewParms.or.axis[1][1];
	viewerMatrix[9] = tr.viewParms.or.axis[1][2];
	viewerMatrix[13] = -origin[0] * viewerMatrix[1] + -origin[1] * viewerMatrix[5] + -origin[2] * viewerMatrix[9];

	viewerMatrix[2] = tr.viewParms.or.axis[2][0];
	viewerMatrix[6] = tr.viewParms.or.axis[2][1];
	viewerMatrix[10] = tr.viewParms.or.axis[2][2];
	viewerMatrix[14] = -origin[0] * viewerMatrix[2] + -origin[1] * viewerMatrix[6] + -origin[2] * viewerMatrix[10];

	viewerMatrix[3] = 0;
	viewerMatrix[7] = 0;
	viewerMatrix[11] = 0;
	viewerMatrix[15] = 1;

	// convert from our coordinate system (looking down X)
	// to OpenGL's coordinate system (looking down -Z)
	myGlMultMatrix( viewerMatrix, s_flipMatrix, tr.or.modelMatrix );

	tr.viewParms.world = tr.or;

}
*)

    /// <summary>
    /// Based on Q3: R_RotateForViewer
    /// RotateForViewer
    ///
    /// Sets up the modelview matrix for a given viewParm
    /// </summary>
    [<Pure>]
    let rotateForViewer (viewParms: ViewParms) =
        
        let axis =
            Axis (
                // Looks like it is a identity matrix
                Vector3 (1.f, 0.f, 0.f),
                Vector3 (0.f, 1.f, 0.f),
                Vector3 (0.f, 0.f, 1.f)
            )

        let viewOrigin = viewParms.Orientation.Origin

        // transform by the camera placement
        let origin = viewParms.Orientation.Origin

        let viewerMatrix =
            Matrix16 (
                viewParms.Orientation.Axis.[0].[0],
                viewParms.Orientation.Axis.[1].[0],
                viewParms.Orientation.Axis.[2].[0],
                0.f,
                viewParms.Orientation.Axis.[0].[1],
                viewParms.Orientation.Axis.[1].[1],
                viewParms.Orientation.Axis.[2].[1],
                0.f,
                viewParms.Orientation.Axis.[0].[2],
                viewParms.Orientation.Axis.[1].[2],
                viewParms.Orientation.Axis.[2].[2],
                0.f,
                -origin.[0] * viewParms.Orientation.Axis.[0].[0] + -origin.[1] * viewParms.Orientation.Axis.[0].[1] + -origin.[2] * viewParms.Orientation.Axis.[0].[2],
                -origin.[0] * viewParms.Orientation.Axis.[1].[0] + -origin.[1] * viewParms.Orientation.Axis.[1].[1] + -origin.[2] * viewParms.Orientation.Axis.[1].[2],
                -origin.[0] * viewParms.Orientation.Axis.[2].[0] + -origin.[1] * viewParms.Orientation.Axis.[2].[1] + -origin.[2] * viewParms.Orientation.Axis.[2].[2],
                1.f
            )
        
        OrientationR (
            Vector3 (),
            axis,
            viewOrigin,
            // convert from our coordinate system (looking down X)
            // to OpenGL's coordinate system (looking down -Z)
            viewerMatrix * flipMatrix
        )

(*
static void SetFarClip( void )
{
	float	farthestCornerDistance = 0;
	int		i;

	// if not rendering the world (icons, menus, etc)
	// set a 2k far clip plane
	if ( tr.refdef.rdflags & RDF_NOWORLDMODEL ) {
		tr.viewParms.zFar = 2048;
		return;
	}

	//
	// set far clipping planes dynamically
	//
	farthestCornerDistance = 0;
	for ( i = 0; i < 8; i++ )
	{
		vec3_t v;
		vec3_t vecTo;
		float distance;

		if ( i & 1 )
		{
			v[0] = tr.viewParms.visBounds[0][0];
		}
		else
		{
			v[0] = tr.viewParms.visBounds[1][0];
		}

		if ( i & 2 )
		{
			v[1] = tr.viewParms.visBounds[0][1];
		}
		else
		{
			v[1] = tr.viewParms.visBounds[1][1];
		}

		if ( i & 4 )
		{
			v[2] = tr.viewParms.visBounds[0][2];
		}
		else
		{
			v[2] = tr.viewParms.visBounds[1][2];
		}

		VectorSubtract( v, tr.viewParms.or.origin, vecTo );

		distance = vecTo[0] * vecTo[0] + vecTo[1] * vecTo[1] + vecTo[2] * vecTo[2];

		if ( distance > farthestCornerDistance )
		{
			farthestCornerDistance = distance;
		}
	}
	tr.viewParms.zFar = sqrt( farthestCornerDistance );
}
*)

    /// <summary>
    /// Based on Q3: SetFarClip
    /// SetFarClip
    /// </summary>
    [<Pure>]
    let setFarClip (rdFlags: RdFlags) (visibilityBounds: Bounds) (orientation: OrientationR) =
        // if not rendering the world (icons, menus, etc)
        // set a 2k far clip plane
        match rdFlags.HasFlag RdFlags.NoWorldModel with
        | true -> 2048.f
        | _ ->

        // set far clipping planes dynamically
        let rec calculateFarthestCornerDistance distance acc =
            match acc with
            | 8 -> distance
            | _ ->
            
            let x = match (acc &&& 1) <> 0 with | true -> visibilityBounds.[0].[0] | _ -> visibilityBounds.[1].[0]
            let y = match (acc &&& 2) <> 0 with | true -> visibilityBounds.[0].[1] | _ -> visibilityBounds.[1].[1]
            let z = match (acc &&& 4) <> 0 with | true -> visibilityBounds.[0].[2] | _ -> visibilityBounds.[1].[2]

            let v = Vector3 (x, y, z)

            let vecTo = v - orientation.Origin
            let possibleDistance = Vector3.dot vecTo vecTo

            calculateFarthestCornerDistance (match possibleDistance > distance with | true -> possibleDistance | _ -> distance) (acc + 1)

        sqrt <| calculateFarthestCornerDistance 0.f 0

(*
void R_SetupProjection( void ) {
	float	xmin, xmax, ymin, ymax;
	float	width, height, depth;
	float	zNear, zFar;

	// dynamically compute far clip plane distance
	SetFarClip();

	//
	// set up projection matrix
	//
	zNear	= r_znear->value;
	zFar	= tr.viewParms.zFar;

	ymax = zNear * tan( tr.refdef.fov_y * M_PI / 360.0f );
	ymin = -ymax;

	xmax = zNear * tan( tr.refdef.fov_x * M_PI / 360.0f );
	xmin = -xmax;

	width = xmax - xmin;
	height = ymax - ymin;
	depth = zFar - zNear;

	tr.viewParms.projectionMatrix[0] = 2 * zNear / width;
	tr.viewParms.projectionMatrix[4] = 0;
	tr.viewParms.projectionMatrix[8] = ( xmax + xmin ) / width;	// normally 0
	tr.viewParms.projectionMatrix[12] = 0;

	tr.viewParms.projectionMatrix[1] = 0;
	tr.viewParms.projectionMatrix[5] = 2 * zNear / height;
	tr.viewParms.projectionMatrix[9] = ( ymax + ymin ) / height;	// normally 0
	tr.viewParms.projectionMatrix[13] = 0;

	tr.viewParms.projectionMatrix[2] = 0;
	tr.viewParms.projectionMatrix[6] = 0;
	tr.viewParms.projectionMatrix[10] = -( zFar + zNear ) / depth;
	tr.viewParms.projectionMatrix[14] = -2 * zFar * zNear / depth;

	tr.viewParms.projectionMatrix[3] = 0;
	tr.viewParms.projectionMatrix[7] = 0;
	tr.viewParms.projectionMatrix[11] = -1;
	tr.viewParms.projectionMatrix[15] = 0;
}
*)

    /// <summary>
    /// Based on Q3: R_SetupProjection
    /// SetupProjection
    /// </summary>
    [<Pure>]
    let setupProjection (zNear: single) (rdFlags: RdFlags) (view: ViewParms) (fovX: single) (fovY: single) =
        // dynamically compute far clip plane distance
        let zFar = setFarClip rdFlags view.VisibilityBounds view.Orientation

        let xMax = zNear * (tan <| fovX * QMath.PI / 360.f)
        let xMin = -xMax

        let yMax = zNear * (tan <| fovY * QMath.PI / 360.f)
        let yMin = -yMax

        let width = xMax - xMin
        let height = yMax - yMin
        let depth = zFar - zNear

        (
            Matrix16 (
                2.f * zNear / width, 0.f, 0.f, 0.f,
                0.f, 2.f * zNear / height, 0.f, 0.f,
                (xMax + xMin) / width, (yMax + yMin) / height, -(zFar + zNear) / depth, -1.f,
                0.f, 0.f, -2.f * zFar * zNear / depth, 0.f
            ),
            zFar
        )

(*
void R_SetupFrustum (void) {
	int		i;
	float	xs, xc;
	float	ang;

	ang = tr.viewParms.fovX / 180 * M_PI * 0.5f;
	xs = sin( ang );
	xc = cos( ang );

	VectorScale( tr.viewParms.or.axis[0], xs, tr.viewParms.frustum[0].normal );
	VectorMA( tr.viewParms.frustum[0].normal, xc, tr.viewParms.or.axis[1], tr.viewParms.frustum[0].normal );

	VectorScale( tr.viewParms.or.axis[0], xs, tr.viewParms.frustum[1].normal );
	VectorMA( tr.viewParms.frustum[1].normal, -xc, tr.viewParms.or.axis[1], tr.viewParms.frustum[1].normal );

	ang = tr.viewParms.fovY / 180 * M_PI * 0.5f;
	xs = sin( ang );
	xc = cos( ang );

	VectorScale( tr.viewParms.or.axis[0], xs, tr.viewParms.frustum[2].normal );
	VectorMA( tr.viewParms.frustum[2].normal, xc, tr.viewParms.or.axis[2], tr.viewParms.frustum[2].normal );

	VectorScale( tr.viewParms.or.axis[0], xs, tr.viewParms.frustum[3].normal );
	VectorMA( tr.viewParms.frustum[3].normal, -xc, tr.viewParms.or.axis[2], tr.viewParms.frustum[3].normal );

	for (i=0 ; i<4 ; i++) {
		tr.viewParms.frustum[i].type = PLANE_NON_AXIAL;
		tr.viewParms.frustum[i].dist = DotProduct (tr.viewParms.or.origin, tr.viewParms.frustum[i].normal);
		SetPlaneSignbits( &tr.viewParms.frustum[i] );
	}
}
*)

    /// <summary>
    /// Based on Q3: R_SetupProjection
    /// SetupFrustum
    /// 
    /// Setup that culling frustum planes for the current view
    /// </summary>
    [<Pure>]
    let setupFrustum (view: ViewParms) =
        let xAngle = view.FovX / 180.f * QMath.PI * 0.5f
        let xs = sin xAngle
        let xc = cos xAngle

        let yAngle = view.FovY / 180.f * QMath.PI * 0.5f
        let ys = sin yAngle
        let yc = cos yAngle

        let xNormal = xs * view.Orientation.Axis.[0]
        let yNormal = ys * view.Orientation.Axis.[0]

        let leftNormal = (xc, view.Orientation.Axis.[1]) *+ xNormal
        let rightNormal = (-xc, view.Orientation.Axis.[1]) *+ xNormal
        let bottomNormal = (yc, view.Orientation.Axis.[2]) *+ yNormal
        let topNormal = (-yc, view.Orientation.Axis.[2]) *+ yNormal

        {
            Left =
                Plane (
                    leftNormal,
                    Vector3.dot view.Orientation.Origin leftNormal,
                    PlaneType.NonAxial
                );
            Right = 
                Plane (
                    rightNormal,
                    Vector3.dot view.Orientation.Origin rightNormal,
                    PlaneType.NonAxial
                );
            Bottom =
                Plane (
                    bottomNormal,
                    Vector3.dot view.Orientation.Origin bottomNormal,
                    PlaneType.NonAxial
                );
            Top =
                Plane (
                    topNormal,
                    Vector3.dot view.Orientation.Origin topNormal,
                    PlaneType.NonAxial
                );
        }

(*
void R_MirrorPoint (vec3_t in, orientation_t *surface, orientation_t *camera, vec3_t out) {
	int		i;
	vec3_t	local;
	vec3_t	transformed;
	float	d;

	VectorSubtract( in, surface->origin, local );

	VectorClear( transformed );
	for ( i = 0 ; i < 3 ; i++ ) {
		d = DotProduct(local, surface->axis[i]);
		VectorMA( transformed, d, camera->axis[i], transformed );
	}

	VectorAdd( transformed, camera->origin, out );
}
*)

    /// <summary>
    /// Based on Q3: R_MirrorPoint
    /// MirrorPoint
    /// </summary>
    [<Pure>]
    let mirrorPoint (v: Vector3) (surface: Orientation) (camera: Orientation) =
        let local = v - surface.Origin
        let rec transform transformed acc =
            match acc with
            | 3 -> transformed
            | _ ->
            transform ((Vector3.dot local surface.Axis.[acc], camera.Axis.[acc]) *+ transformed) (acc + 1)

        (transform (Vector3.zero) 0) + camera.Origin

(*
void R_MirrorVector (vec3_t in, orientation_t *surface, orientation_t *camera, vec3_t out) {
	int		i;
	float	d;

	VectorClear( out );
	for ( i = 0 ; i < 3 ; i++ ) {
		d = DotProduct(in, surface->axis[i]);
		VectorMA( out, d, camera->axis[i], out );
	}
}
*)

    /// <summary>
    /// Based on Q3: R_MirrorVector
    /// MirrorVector
    /// </summary>
    [<Pure>]
    let mirrorVector (v: Vector3) (surface: Orientation) (camera: Orientation) =
        let rec transform transformed acc =
            match acc with
            | 3 -> transformed
            | _ ->
            transform ((Vector3.dot v surface.Axis.[acc], camera.Axis.[acc]) *+ transformed) (acc + 1)

        transform (Vector3.zero) 0

(*
void R_PlaneForSurface (surfaceType_t *surfType, cplane_t *plane) {
	srfTriangles_t	*tri;
	srfPoly_t		*poly;
	drawVert_t		*v1, *v2, *v3;
	vec4_t			plane4;

	if (!surfType) {
		Com_Memset (plane, 0, sizeof( *plane));
		plane->normal[0] = 1;
		return;
	}
	switch ( *surfType) {
	case SF_FACE:
		*plane = ((srfSurfaceFace_t* )surfType)->plane;
		return;
	case SF_TRIANGLES:
		tri = (srfTriangles_t* )surfType;
		v1 = tri->verts + tri->indexes[0];
		v2 = tri->verts + tri->indexes[1];
		v3 = tri->verts + tri->indexes[2];
		PlaneFromPoints( plane4, v1->xyz, v2->xyz, v3->xyz );
		VectorCopy( plane4, plane->normal ); 
		plane->dist = plane4[3];
		return;
	case SF_POLY:
		poly = (srfPoly_t* )surfType;
		PlaneFromPoints( plane4, poly->verts[0].xyz, poly->verts[1].xyz, poly->verts[2].xyz );
		VectorCopy( plane4, plane->normal ); 
		plane->dist = plane4[3];
		return;
	default:
		Com_Memset (plane, 0, sizeof( *plane));
		plane->normal[0] = 1;		
		return;
	}
}
*)

    /// <summary>
    /// Based on Q3: R_PlaneForSurface
    /// PlaneForSurface
    /// </summary>
    [<Pure>]
    let planeForSurface (surface: Surface) (plane: Plane) =
        match surface with
        | Face (value) ->
            value.Plane
        | Triangles (value) ->
            let vertices = value.Vertices
            let indices = value.Indices
            let v1 = vertices.[indices.[0]]
            let v2 = vertices.[indices.[1]]
            let v3 = vertices.[indices.[2]]
            let plane4 = Plane.ofPoints v1.Vertex v2.Vertex v3.Vertex

            Plane (plane4.Normal, plane4.Distance, plane.Type, plane.SignBits)
        | Poly (value) ->
            let vertices = value.Vertices
            let plane4 = Plane.ofPoints vertices.[0].Vertex vertices.[1].Vertex vertices.[2].Vertex

            Plane (plane4.Normal, plane4.Distance, plane.Type, plane.SignBits)
        | _ ->
            Plane (Vector3 (1.f, 0.f, 0.f), 0.f, PlaneType.X, 0uy)

(*
/*
=================
R_GetPortalOrientation

entityNum is the entity that the portal surface is a part of, which may
be moving and rotating.

Returns qtrue if it should be mirrored
=================
*/
qboolean R_GetPortalOrientations( drawSurf_t *drawSurf, int entityNum, 
							 orientation_t *surface, orientation_t *camera,
							 vec3_t pvsOrigin, qboolean *mirror ) {
	int			i;
	cplane_t	originalPlane, plane;
	trRefEntity_t	*e;
	float		d;
	vec3_t		transformed;

	// create plane axis for the portal we are seeing
	R_PlaneForSurface( drawSurf->surface, &originalPlane );

	// rotate the plane if necessary
	if ( entityNum != ENTITYNUM_WORLD ) {
		tr.currentEntityNum = entityNum;
		tr.currentEntity = &tr.refdef.entities[entityNum];

		// get the orientation of the entity
		R_RotateForEntity( tr.currentEntity, &tr.viewParms, &tr.or );

		// rotate the plane, but keep the non-rotated version for matching
		// against the portalSurface entities
		R_LocalNormalToWorld( originalPlane.normal, plane.normal );
		plane.dist = originalPlane.dist + DotProduct( plane.normal, tr.or.origin );

		// translate the original plane
		originalPlane.dist = originalPlane.dist + DotProduct( originalPlane.normal, tr.or.origin );
	} else {
		plane = originalPlane;
	}

	VectorCopy( plane.normal, surface->axis[0] );
	PerpendicularVector( surface->axis[1], surface->axis[0] );
	CrossProduct( surface->axis[0], surface->axis[1], surface->axis[2] );

	// locate the portal entity closest to this plane.
	// origin will be the origin of the portal, origin2 will be
	// the origin of the camera
	for ( i = 0 ; i < tr.refdef.num_entities ; i++ ) {
		e = &tr.refdef.entities[i];
		if ( e->e.reType != RT_PORTALSURFACE ) {
			continue;
		}

		d = DotProduct( e->e.origin, originalPlane.normal ) - originalPlane.dist;
		if ( d > 64 || d < -64) {
			continue;
		}

		// get the pvsOrigin from the entity
		VectorCopy( e->e.oldorigin, pvsOrigin );

		// if the entity is just a mirror, don't use as a camera point
		if ( e->e.oldorigin[0] == e->e.origin[0] && 
			e->e.oldorigin[1] == e->e.origin[1] && 
			e->e.oldorigin[2] == e->e.origin[2] ) {
			VectorScale( plane.normal, plane.dist, surface->origin );
			VectorCopy( surface->origin, camera->origin );
			VectorSubtract( vec3_origin, surface->axis[0], camera->axis[0] );
			VectorCopy( surface->axis[1], camera->axis[1] );
			VectorCopy( surface->axis[2], camera->axis[2] );

			*mirror = qtrue;
			return qtrue;
		}

		// project the origin onto the surface plane to get
		// an origin point we can rotate around
		d = DotProduct( e->e.origin, plane.normal ) - plane.dist;
		VectorMA( e->e.origin, -d, surface->axis[0], surface->origin );
			
		// now get the camera origin and orientation
		VectorCopy( e->e.oldorigin, camera->origin );
		AxisCopy( e->e.axis, camera->axis );
		VectorSubtract( vec3_origin, camera->axis[0], camera->axis[0] );
		VectorSubtract( vec3_origin, camera->axis[1], camera->axis[1] );

		// optionally rotate
		if ( e->e.oldframe ) {
			// if a speed is specified
			if ( e->e.frame ) {
				// continuous rotate
				d = (tr.refdef.time/1000.0f) * e->e.frame;
				VectorCopy( camera->axis[1], transformed );
				RotatePointAroundVector( camera->axis[1], camera->axis[0], transformed, d );
				CrossProduct( camera->axis[0], camera->axis[1], camera->axis[2] );
			} else {
				// bobbing rotate, with skinNum being the rotation offset
				d = sin( tr.refdef.time * 0.003f );
				d = e->e.skinNum + d * 4;
				VectorCopy( camera->axis[1], transformed );
				RotatePointAroundVector( camera->axis[1], camera->axis[0], transformed, d );
				CrossProduct( camera->axis[0], camera->axis[1], camera->axis[2] );
			}
		}
		else if ( e->e.skinNum ) {
			d = e->e.skinNum;
			VectorCopy( camera->axis[1], transformed );
			RotatePointAroundVector( camera->axis[1], camera->axis[0], transformed, d );
			CrossProduct( camera->axis[0], camera->axis[1], camera->axis[2] );
		}
		*mirror = qfalse;
		return qtrue;
	}

	// if we didn't locate a portal entity, don't render anything.
	// We don't want to just treat it as a mirror, because without a
	// portal entity the server won't have communicated a proper entity set
	// in the snapshot

	// unfortunately, with local movement prediction it is easily possible
	// to see a surface before the server has communicated the matching
	// portal surface entity, so we don't want to print anything here...

	//ri.Printf( PRINT_ALL, "Portal surface without a portal entity\n" );

	return qfalse;
}
*)

    // This is for GetPortalOrientation
    //// create plane axis for the portal we are seeing
    [<Pure>]
    let createPlaneAxis (drawSurface: DrawSurface) =
        planeForSurface drawSurface.Surface Plane.zero

    /// <summary>
    /// Based on Q3: R_GetPortalOrientation
    /// GetPortalOrientation
    ///
    /// entityId is the entity that the portal surface is a part of, which may
    /// be moving and rotating.
    ///
    /// Returns true if it should be mirrored
    /// </summary>
    let getPortalOrientation (drawSurface: DrawSurface) (entityId: int) (surface: Orientation) (camera: Orientation) (pvsOrigin: Vector3) (tr: TrGlobals) =
        // create plane axis for the portal we are seeing
        let originalPlane = createPlaneAxis drawSurface

        // rotate the plane if necessary
        match entityId <> Constants.EntityIdWorld with
        | false -> (originalPlane, originalPlane, tr)
        | _ ->

        let tr = TrGlobals.updateCurrentEntityById entityId tr
        match tr.CurrentEntity with
        | None -> raise <| Exception "Current entity does not exist."
        | Some (entity) ->

        // get the orientation of the entity
        let orientation = rotateForEntity entity tr.ViewParms tr.Orientation

        // rotate the plane, but keep the non-rotated version for matching
        // against the portalSurface entities
        let normal = localNormalToWorld originalPlane.Normal orientation
        let distance = originalPlane.Distance + Vector3.dot normal orientation.Origin

        // translate the original plane
        let originalDistance = originalPlane.Distance + Vector3.dot originalPlane.Normal orientation.Origin

        (
            Plane.updateDistance originalDistance originalPlane,
            Plane (normal, distance, PlaneType.X, 0uy),
            { tr with Orientation = orientation }
        )



(*
static qboolean IsMirror( const drawSurf_t *drawSurf, int entityNum )
{
	int			i;
	cplane_t	originalPlane, plane;
	trRefEntity_t	*e;
	float		d;

	// create plane axis for the portal we are seeing
	R_PlaneForSurface( drawSurf->surface, &originalPlane );

	// rotate the plane if necessary
	if ( entityNum != ENTITYNUM_WORLD ) 
	{
		tr.currentEntityNum = entityNum;
		tr.currentEntity = &tr.refdef.entities[entityNum];

		// get the orientation of the entity
		R_RotateForEntity( tr.currentEntity, &tr.viewParms, &tr.or );

		// rotate the plane, but keep the non-rotated version for matching
		// against the portalSurface entities
		R_LocalNormalToWorld( originalPlane.normal, plane.normal );
		plane.dist = originalPlane.dist + DotProduct( plane.normal, tr.or.origin );

		// translate the original plane
		originalPlane.dist = originalPlane.dist + DotProduct( originalPlane.normal, tr.or.origin );
	} 
	else 
	{
		plane = originalPlane;
	}

	// locate the portal entity closest to this plane.
	// origin will be the origin of the portal, origin2 will be
	// the origin of the camera
	for ( i = 0 ; i < tr.refdef.num_entities ; i++ ) 
	{
		e = &tr.refdef.entities[i];
		if ( e->e.reType != RT_PORTALSURFACE ) {
			continue;
		}

		d = DotProduct( e->e.origin, originalPlane.normal ) - originalPlane.dist;
		if ( d > 64 || d < -64) {
			continue;
		}

		// if the entity is just a mirror, don't use as a camera point
		if ( e->e.oldorigin[0] == e->e.origin[0] && 
			e->e.oldorigin[1] == e->e.origin[1] && 
			e->e.oldorigin[2] == e->e.origin[2] ) 
		{
			return qtrue;
		}

		return qfalse;
	}
	return qfalse;
}
*)

    /// <summary>
    /// Based on Q3: IsMirror
    /// IsMirror
    /// </summary>
    // Note: this is internal
    let isMirror (drawSurface: DrawSurface) (entity: TrRefEntity) =
        ()