/// <summary>
/// Page HTML de l'écran de verrouillage.
/// </summary>
public static class LockScreenHtml
{
    public const string Page = """
<!DOCTYPE html>
<html lang="fr">
<head>
<meta charset="utf-8">
<title>Cyber Manager</title>
<style>
:root { --bg:#0b1226; --panel:#131c3a; --line:rgba(255,255,255,.09); --txt:#e2e8f0; --mut:#94a3b8;
        --violet:#8b5cf6; --ambre:#f59e0b; --ok:#22c55e; --ko:#ef4444; }
* { box-sizing:border-box; margin:0; padding:0; user-select:none; -webkit-user-select:none; }
html, body { height:100%; overflow:hidden; }
body { font-family:'Segoe UI', system-ui, sans-serif; color:var(--txt);
       background: radial-gradient(1200px 600px at 75% 15%, rgba(139,92,246,.18), transparent 60%), var(--bg);
       display:flex; flex-direction:column; align-items:center; justify-content:center; }
.clock { position:absolute; top:30px; right:40px; text-align:right; }
.clock b { font-size:34px; font-weight:700; display:block; line-height:1; }
.clock span { font-size:12px; color:var(--mut); text-transform:capitalize; }

.hero { text-align:center; display:flex; flex-direction:column; align-items:center; margin-bottom:40px; }
.logo { width:112px; height:112px; border-radius:30px; display:flex; align-items:center; justify-content:center;
        background:linear-gradient(135deg,#7c3aed,#4f46e5); box-shadow:0 20px 60px rgba(124,58,237,.45); margin-bottom:22px; }
.hero h1 { font-size:46px; letter-spacing:3px; font-weight:800; }
.hero .sub { color:var(--mut); letter-spacing:4px; font-size:13px; margin-top:6px; text-transform:uppercase; }
.poste { margin-top:34px; font-size:30px; font-weight:800; padding:10px 28px; border-radius:16px;
         background:rgba(255,255,255,.05); border:1px solid var(--line); }

.actions { display:flex; gap:20px; margin-top:20px; }
.btn-sq { border:0; border-radius:12px; cursor:pointer; font-family:inherit; font-size:22px; font-weight:800;
          letter-spacing:2px; color:#fff; width:220px; height:80px; display:flex; align-items:center; justify-content:center;
          transition:transform .12s, filter .12s; }
.btn-sq:hover:not(:disabled) { transform:translateY(-2px); filter:brightness(1.08); }
.btn-sq:active:not(:disabled) { transform:translateY(0); }
.btn-sq:disabled { cursor:not-allowed; background:#334155; box-shadow:none; color:#94a3b8; }
#btn-activer { background:linear-gradient(135deg,#f59e0b,#d97706); box-shadow:0 12px 34px rgba(245,158,11,.35); }
#btn-reprendre { background:linear-gradient(135deg,#10b981,#059669); box-shadow:0 12px 34px rgba(16,185,129,.35); }
#btn-quitter { background:linear-gradient(135deg,#ef4444,#b91c1c); box-shadow:0 12px 34px rgba(239,68,68,.35); width:140px; }

.msg { min-height:22px; text-align:center; font-size:16px; margin-top:20px; color:var(--mut); font-weight:600; }
.msg.err { color:#fca5a5; } .msg.ok { color:#86efac; }
.spin { display:inline-block; width:20px; height:20px; border:3px solid rgba(255,255,255,.3); border-top-color:#fff;
        border-radius:50%; animation:r .8s linear infinite; vertical-align:-4px; margin-right:10px; }
@keyframes r { to { transform:rotate(360deg); } }
</style>
</head>
<body>

<div class="clock"><b id="heure">--:--</b><span id="date"></span></div>

<div class="hero">
    <div class="logo">
      <svg width="64" height="64" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/><circle cx="12" cy="10" r="3"/><path d="M9 10h6M12 7c1.5 1.8 1.5 4.2 0 6M12 7c-1.5 1.8-1.5 4.2 0 6"/></svg>
    </div>
    <h1 id="main-titre">CYBER MANAGER</h1>
    <div class="sub" id="main-sous-titre">Gestion de cybercafé</div>
    <div class="poste" id="poste">—</div>
</div>

<div class="actions" id="zone-actions"></div>

<div class="msg" id="msg"></div>

<div id="arret" style="position:fixed; inset:0; z-index:50; display:none; align-items:center; justify-content:center; background:rgba(11,18,38,.94);">
  <div style="text-align:center; padding:40px 56px; border-radius:24px; background:var(--panel); border:1px solid var(--line);">
    <div style="font-size:22px; font-weight:700; margin-bottom:10px;" id="arretTxt"></div>
    <div style="font-size:96px; font-weight:800; color:var(--ambre); line-height:1; margin-bottom:22px;" id="arretNum"></div>
    <button id="arretAnnuler" style="border:0; border-radius:14px; padding:14px 28px; font-family:inherit; font-size:16px; font-weight:700; color:#fff; background:#475569; cursor:pointer;">Annuler l'extinction</button>
  </div>
</div>

<script>
const $ = id => document.getElementById(id);
const msg = $('msg');
let etat = 'serveur_injoignable', occupe = false;

function afficherEtat() {
  const zone = $('zone-actions');
  msg.className = 'msg';
  
  if (etat === 'serveur_injoignable') {
    $('main-titre').textContent = "CYBER MANAGER";
    $('main-sous-titre').textContent = "Gestion de cybercafé";
    zone.innerHTML = '';
    msg.textContent = "Le serveur est injoignable. Nouvelle tentative en cours…";
    return;
  }

  if (etat === 'occupe') {
    $('main-titre').textContent = "Poste en cours d'utilisation";
    $('main-sous-titre').textContent = "";
    zone.innerHTML = '';
    msg.textContent = "";
    return;
  }

  if (etat === 'suspendu') {
    $('main-titre').textContent = "Session en pause";
    $('main-sous-titre').textContent = "Vous pouvez reprendre ou quitter.";
    zone.innerHTML = `<button id="btn-reprendre" class="btn-sq" onclick="postAction('reprendre')">REPRENDRE</button>
                      <button id="btn-quitter" class="btn-sq" onclick="postAction('quitter')">QUITTER</button>`;
    msg.textContent = "";
    return;
  }

  if (etat === 'desactive') {
    $('main-titre').textContent = "Ce poste de travail est désactivé";
    $('main-sous-titre').textContent = "Contactez le gérant pour l'activer";
    zone.innerHTML = `<button class="btn-sq" style="width:260px; background:#334155; color:#94a3b8; cursor:not-allowed;" disabled>DÉSACTIVÉ</button>`;
    msg.textContent = "";
    return;
  }

  if (etat === 'disponible') {
    $('main-titre').textContent = "Poste disponible";
    $('main-sous-titre').textContent = "";
    zone.innerHTML = `<button id="btn-activer" class="btn-sq" style="width:260px;" onclick="postAction('activer')">ACTIVER</button>`;
    msg.textContent = "";
    return;
  }
}

function postAction(action) {
    if (occupe) return;
    occupe = true;
    msg.innerHTML = '<span class="spin"></span>Traitement en cours...';
    msg.className = 'msg';
    window.chrome.webview.postMessage(action);
    setTimeout(() => { occupe = false; }, 3000);
}

const arret = $('arret');
function majArret(r) {
  if (r > 0) {
    arret.style.display = 'flex';
    $('arretTxt').textContent = 'Extinction du poste dans';
    $('arretNum').textContent = r;
    $('arretAnnuler').style.display = '';
  } else if (r === 0) {
    arret.style.display = 'flex';
    $('arretTxt').textContent = 'Extinction en cours…';
    $('arretNum').textContent = '';
    $('arretAnnuler').style.display = 'none';
  } else {
    arret.style.display = 'none';
    msg.textContent = r === -1 ? 'Extinction annulée.' : "L'extinction a échoué.";
    setTimeout(() => afficherEtat(), 4000);
  }
}
$('arretAnnuler').addEventListener('click', () => window.chrome.webview.postMessage('annuler_arret'));

window.chrome.webview.addEventListener('message', ev => {
  const d = ev.data || {};
  if (d.type === 'infos') {
    $('poste').textContent = d.poste || '—';
    etat = d.etat || 'serveur_injoignable';
    afficherEtat();
  } else if (d.type === 'activation') {
    if (d.statut === 'encours') {
      msg.innerHTML = '<span class="spin"></span>Activation…';
    } else if (d.statut === 'ok') {
      msg.textContent = d.message || 'Poste activé. Bonne navigation !';
      msg.className = 'msg ok';
    } else {
      occupe = false;
      msg.textContent = d.message || 'Activation impossible.';
      msg.className = 'msg err';
    }
  } else if (d.type === 'arret') {
    majArret(d.restant);
  }
});

function horloge() {
  const d = new Date();
  $('heure').textContent = String(d.getHours()).padStart(2,'0') + ':' + String(d.getMinutes()).padStart(2,'0');
  $('date').textContent = d.toLocaleDateString('fr-FR', { weekday:'long', day:'numeric', month:'long' });
}
horloge(); setInterval(horloge, 1000);

document.addEventListener('contextmenu', e => e.preventDefault());
afficherEtat();
</script>
</body>
</html>
""";
}