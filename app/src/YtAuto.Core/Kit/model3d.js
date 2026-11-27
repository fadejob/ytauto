// Real 3D objects in an OpenFilm page (three.js, MIT, bundled in kit/three). The picture depends only on t.
// The page needs this import map BEFORE its module script:
//   <script type="importmap">{"imports":{"three":"./kit/three/three.module.min.js","three/addons/":"./kit/three/addons/"}}</script>
// Usage:
//   import { model3d } from "./kit/model3d.js";
//   const bee = model3d(document.getElementById("m"), "assets/stock/s03/model1/model.glb", { spin: 50 });
//   K.page(D, (t) => bee.draw(t), { ready: () => bee.load() });
import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";

/**
 * Loads a .glb/.gltf into a transparent canvas filling `container` and returns { ready, draw(t) }.
 * Options: spin (deg/s turntable), tilt (deg camera elevation), start (deg), zoom (1 = fits), float (px bob),
 * exposure, push (extra zoom over the clip: 0.15 = 15 %), duration (clip length for push).
 */
export function model3d(container, url, opts = {}) {
  const o = { spin: 40, tilt: 14, start: 25, zoom: 1, float: 0, exposure: 1.05, push: 0, duration: 4, ...opts };
  const rect = container.getBoundingClientRect();
  const w = Math.max(2, Math.round(rect.width)), h = Math.max(2, Math.round(rect.height));
  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(1);
  renderer.setSize(w, h, false);
  renderer.setClearColor(0x000000, 0);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = o.exposure;
  renderer.domElement.style.cssText = "position:absolute;inset:0;width:100%;height:100%";
  container.appendChild(renderer.domElement);

  const scene = new THREE.Scene();
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(renderer), 0.04).texture;
  const key = new THREE.DirectionalLight(0xffffff, 2.2);
  key.position.set(3, 5, 4);
  scene.add(key, new THREE.HemisphereLight(0xffffff, 0x202030, 0.6));
  const camera = new THREE.PerspectiveCamera(30, w / h, 0.01, 1000);
  const pivot = new THREE.Group();
  scene.add(pivot);
  let radius = 1;

  // Loading starts only when the host asks for `ready` (a page opened just to read its duration starts no download).
  let loading = null;
  const load = () => loading ??= fetchModel(url).then(({ data, path, manager }) => new GLTFLoader(manager).parseAsync(data, path)).then((gltf) => {
    const obj = gltf.scene;
    const box = new THREE.Box3().setFromObject(obj);
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());
    obj.position.sub(center);                         // centre on the turntable
    radius = Math.max(size.length() / 2, 1e-4);
    pivot.add(obj);
  });

  function draw(t) {
    const fov = THREE.MathUtils.degToRad(camera.fov);
    const aspectFit = Math.min(1, camera.aspect);     // portrait stages: fit by width
    const p = Math.min(1, Math.max(0, t / Math.max(0.01, o.duration)));
    const dist = (radius / Math.sin(fov / 2)) / aspectFit / o.zoom / (1 + o.push * (p * p * (3 - 2 * p)));
    const el = THREE.MathUtils.degToRad(o.tilt);
    camera.position.set(0, Math.sin(el) * dist, Math.cos(el) * dist);
    camera.lookAt(0, 0, 0);
    camera.near = dist / 100; camera.far = dist * 10; camera.updateProjectionMatrix();
    pivot.rotation.y = THREE.MathUtils.degToRad(o.start + o.spin * t);
    pivot.position.y = o.float ? Math.sin(t * 1.4) * radius * 0.04 * o.float : 0;
    renderer.render(scene, camera);
  }
  return { get ready() { return load(); }, load, draw, renderer };
}

/**
 * Every file of the model is read with a plain fetch and handed to three.js from memory (blob: URLs): three's own
 * loader requests are cut short when OpenFilm puts a page to rest, which counts as a failed file.
 */
async function fetchModel(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`model ${url}: ${res.status}`);
  const data = await res.arrayBuffer();
  const path = url.slice(0, url.lastIndexOf("/") + 1);
  const manager = new THREE.LoadingManager();
  const head = new TextDecoder().decode(new Uint8Array(data, 0, Math.min(4, data.byteLength)));
  if (head !== "glTF") {                                        // .gltf (JSON): its .bin and textures are separate files
    const json = JSON.parse(new TextDecoder().decode(data));
    const uris = [...(json.buffers ?? []), ...(json.images ?? [])].map((x) => x.uri).filter((u) => u && !/^(data|blob):/.test(u));
    const blobs = new Map();
    await Promise.all(uris.map(async (u) => {
      const r = await fetch(path + u);
      if (!r.ok) throw new Error(`model file ${u}: ${r.status}`);
      blobs.set(new URL(path + u, location.href).href, URL.createObjectURL(await r.blob()));
    }));
    manager.setURLModifier((u) => blobs.get(new URL(u, location.href).href) ?? u);
  }
  return { data, path, manager };
}
