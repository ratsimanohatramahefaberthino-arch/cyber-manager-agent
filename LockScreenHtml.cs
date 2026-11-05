/// <summary>
/// Page HTML de l'écran de verrouillage (affichée dans WebView2).
/// Aucune valeur dynamique n'est injectée ici : l'agent envoie les infos
/// (poste, IP, état, messages…) par messages WebView2 → pas de chaîne interpolée, pas de souci d'échappement.
/// Les textes reçus sont insérés via textContent (jamais innerHTML).
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
       display:flex; flex-direction:column; }
.top { display:flex; justify-content:space-between; align-items:center; padding:22px 36px; }
.brand { display:flex; align-items:center; gap:12px; font-weight:700; letter-spacing:2px; font-size:15px; }
.brand small { display:block; color:var(--ambre); font-size:10px; letter-spacing:3px; font-weight:600; }
.top-droite { display:flex; align-items:center; gap:22px; }
.chatbtn { border:1px solid var(--line); background:rgba(255,255,255,.06); color:var(--txt); border-radius:12px;
           padding:9px 16px; font-family:inherit; font-size:13px; font-weight:600; cursor:pointer; transition:background .15s; }
.chatbtn:hover { background:rgba(255,255,255,.12); }
.clock { text-align:right; }
.clock b { font-size:34px; font-weight:700; display:block; line-height:1; }
.clock span { font-size:12px; color:var(--mut); text-transform:capitalize; }

.main { flex:1; display:grid; grid-template-columns: 380px 1fr; gap:26px; padding:0 36px 10px; min-height:0; }
.guide { background:var(--panel); border:1px solid var(--line); border-radius:20px; padding:28px; overflow:auto; }
.guide h2 { font-size:20px; margin-bottom:6px; }
.guide p.lead { color:var(--mut); font-size:14px; margin-bottom:18px; }
.guide ol { list-style:none; counter-reset:n; display:flex; flex-direction:column; gap:14px; }
.guide li { counter-increment:n; display:flex; gap:12px; font-size:14px; line-height:1.45; }
.guide li::before { content:counter(n); flex:none; width:26px; height:26px; border-radius:50%;
                    background:rgba(139,92,246,.25); color:#c4b5fd; font-weight:700; font-size:13px;
                    display:flex; align-items:center; justify-content:center; }
.guide .note { margin-top:22px; padding:12px 14px; border-radius:12px; font-size:12.5px; color:#fcd34d;
               background:rgba(245,158,11,.1); border:1px solid rgba(245,158,11,.25); }

.hero { display:flex; flex-direction:column; align-items:center; justify-content:center; text-align:center; }
.logo { width:112px; height:112px; border-radius:30px; display:flex; align-items:center; justify-content:center;
        background:linear-gradient(135deg,#7c3aed,#4f46e5); box-shadow:0 20px 60px rgba(124,58,237,.45); margin-bottom:22px; }
.hero h1 { font-size:46px; letter-spacing:3px; font-weight:800; }
.hero .sub { color:var(--mut); letter-spacing:4px; font-size:13px; margin-top:6px; text-transform:uppercase; }
.poste { margin-top:34px; font-size:30px; font-weight:800; padding:10px 28px; border-radius:16px;
         background:rgba(255,255,255,.05); border:1px solid var(--line); }

.bar { display:grid; grid-template-columns: 1fr 1fr 1fr 260px; gap:14px; padding:18px 36px 26px; align-items:stretch; }
.box { background:var(--panel); border:1px solid var(--line); border-radius:16px; padding:12px 18px; }
.box .t { font-size:11px; letter-spacing:2px; text-transform:uppercase; color:var(--mut); margin-bottom:6px; }
.box .v { font-size:15px; font-weight:600; display:flex; align-items:center; gap:8px; }
.dot { width:10px; height:10px; border-radius:50%; background:var(--ko); flex:none; }
.dot.ok { background:var(--ok); box-shadow:0 0 10px var(--ok); }
.dot.warn { background:var(--ambre); }

#activer { border:0; border-radius:18px; cursor:pointer; font-family:inherit; font-size:24px; font-weight:800;
           letter-spacing:3px; color:#fff; background:linear-gradient(135deg,#f59e0b,#d97706);
           box-shadow:0 12px 34px rgba(245,158,11,.35); transition:transform .12s, filter .12s; }
#activer:hover:not(:disabled) { transform:translateY(-2px); filter:brightness(1.08); }
#activer:active:not(:disabled) { transform:translateY(0); }
#activer:disabled { cursor:not-allowed; background:#334155; box-shadow:none; color:#94a3b8; font-size:17px; letter-spacing:1px; }

#bandeau { display:none; margin:0 36px 10px; padding:12px 18px; border-radius:14px; text-align:center; font-size:15px;
           font-weight:700; color:#fde68a; background:rgba(245,158,11,.14); border:1px solid rgba(245,158,11,.35); }
#msg { min-height:22px; text-align:center; font-size:14px; padding:0 36px 14px; color:var(--mut); }
#msg.err { color:#fca5a5; } #msg.ok { color:#86efac; }
.spin { display:inline-block; width:14px; height:14px; border:2px solid rgba(255,255,255,.3); border-top-color:#fff;
        border-radius:50%; animation:r .8s linear infinite; vertical-align:-2px; margin-right:8px; }
@keyframes r { to { transform:rotate(360deg); } }

/* Discussion avec l'accueil */
#chat { position:fixed; right:36px; bottom:170px; width:360px; height:420px; display:flex; flex-direction:column; z-index:20;
        background:var(--panel); border:1px solid var(--line); border-radius:18px; box-shadow:0 24px 60px rgba(0,0,0,.5);
        opacity:0; transform:translateY(16px) scale(.98); pointer-events:none; transition:opacity .22s, transform .22s; }
#chat.ouvert { opacity:1; transform:none; pointer-events:auto; }
.ch-tete { display:flex; justify-content:space-between; align-items:center; padding:14px 16px; border-bottom:1px solid var(--line);
           font-size:12px; letter-spacing:2px; text-transform:uppercase; color:#c4b5fd; }
.ch-tete button { border:0; background:none; color:var(--mut); font-size:16px; cursor:pointer; }
#chatListe { flex:1; overflow-y:auto; padding:14px; display:flex; flex-direction:column; gap:10px; }
#chatListe .vide { margin:auto; color:var(--mut); font-size:13px; }
.m { display:flex; flex-direction:column; max-width:85%; }
.m.moi { align-self:flex-end; align-items:flex-end; } .m.eux { align-self:flex-start; }
.m .b { padding:8px 12px; border-radius:14px; font-size:14px; line-height:1.4; word-break:break-word; user-select:text; -webkit-user-select:text; }
.m.moi .b { background:#6d28d9; } .m.eux .b { background:#1e293b; border:1px solid var(--line); }
.m .h { font-size:10px; color:var(--mut); margin-top:3px; }
#chatErr { min-height:16px; padding:0 16px; font-size:12px; color:#fca5a5; }
.ch-saisie { display:flex; gap:8px; padding:10px 12px 14px; }
.ch-saisie input { flex:1; border:1px solid var(--line); background:#0b1226; color:var(--txt); border-radius:10px; padding:10px 12px;
                   font-family:inherit; font-size:14px; user-select:text; -webkit-user-select:text; outline:none; }
.ch-saisie input:focus { border-color:var(--violet); }
.ch-saisie button { border:0; border-radius:10px; padding:0 16px; background:var(--violet); color:#fff; font-family:inherit;
                    font-weight:700; cursor:pointer; }
.ch-saisie button:disabled { opacity:.5; cursor:wait; }

/* Décompte d'extinction */
#arret { position:fixed; inset:0; z-index:50; display:none; align-items:center; justify-content:center; background:rgba(11,18,38,.94); }
#arret.show { display:flex; }
#arret .carte { text-align:center; padding:40px 56px; border-radius:24px; background:var(--panel); border:1px solid var(--line); }
#arret .gros { font-size:22px; font-weight:700; margin-bottom:10px; }
#arret .num { font-size:96px; font-weight:800; color:var(--ambre); line-height:1; margin-bottom:22px; }
#arret button { border:0; border-radius:14px; padding:14px 28px; font-family:inherit; font-size:16px; font-weight:700;
                color:#fff; background:#475569; cursor:pointer; }
#arret button:hover { background:#64748b; }

@media (max-width: 1000px) { .main { grid-template-columns:1fr; } .guide { display:none; } .bar { grid-template-columns:1fr 1fr; } }
</style>
</head>
<body>
<div class="top">
  <div class="brand">
    <svg width="30" height="30" viewBox="0 0 24 24" fill="none" stroke="#a78bfa" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg>
    <div>CYBER MANAGER<small>ADMINISTRATION DE CYBERCAFÉ</small></div>
  </div>
  <div class="top-droite">
    <button class="chatbtn" id="btnChat">✉ Écrire à l'accueil</button>
    <div class="clock"><b id="heure">--:--</b><span id="date"></span></div>
  </div>
</div>

<div class="main">
  <section class="guide">
    <h2>Bienvenue</h2>
    <p class="lead">Vous pouvez utiliser cet ordinateur en libre-service.</p>
    <ol>
      <li><span>Cliquez sur le bouton <b>ACTIVER</b> en bas à droite.</span></li>
      <li><span>Votre consommation est <b>comptée automatiquement</b> dès l'activation.</span></li>
      <li><span>Internet est disponible tout de suite. Une petite fenêtre affiche votre temps et votre montant.</span></li>
      <li><span>À la fin, cliquez sur <b>Quitter</b> ou prévenez l'accueil pour régler.</span></li>
    </ol>
    <div class="note">Le tarif en vigueur est affiché à l'accueil.</div>
  </section>

  <section class="hero">
    <div class="logo">
      <svg width="64" height="64" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/><circle cx="12" cy="10" r="3"/><path d="M9 10h6M12 7c1.5 1.8 1.5 4.2 0 6M12 7c-1.5 1.8-1.5 4.2 0 6"/></svg>
    </div>
    <h1>CYBER MANAGER</h1>
    <div class="sub">Gestion de cybercafé</div>
    <div class="poste" id="poste">—</div>
  </section>
</div>

<div id="bandeau"></div>
<div id="msg"></div>

<div class="bar">
  <div class="box"><div class="t">Type de session</div><div class="v">Libre-service (postpayé)</div></div>
  <div class="box"><div class="t">Poste</div><div class="v"><span id="ip">—</span></div></div>
  <div class="box"><div class="t">Serveur</div><div class="v"><span class="dot" id="dot"></span><span id="etatTxt">Connexion…</span></div></div>
  <button id="activer" disabled>ACTIVER</button>
</div>

<div id="chat">
  <div class="ch-tete"><b>Messages — Accueil</b><button id="chatFermer" title="Fermer">✕</button></div>
  <div id="chatListe"><p class="vide" id="chatVide">Aucun message pour le moment.</p></div>
  <div id="chatErr"></div>
  <div class="ch-saisie">
    <input id="chatSaisie" maxlength="500" placeholder="Écrire à l'accueil…" autocomplete="off">
    <button id="chatEnvoyer">Envoyer</button>
  </div>
</div>

<div id="arret">
  <div class="carte">
    <div class="gros" id="arretTxt"></div>
    <div class="num" id="arretNum"></div>
    <button id="arretAnnuler">Annuler l'extinction</button>
  </div>
</div>

<script>
const $ = id => document.getElementById(id);
const bouton = $('activer'), msg = $('msg');
let etat = 'serveur_injoignable', occupe = false;

const ETATS = {
  disponible:          { txt:'Connecté',               dot:'ok',   aide:'Cliquez sur ACTIVER pour démarrer votre session.', libre:true },
  occupe:              { txt:'Session en cours',       dot:'ok',   aide:'', libre:false },
  suspendu:            { txt:'Session en pause',       dot:'warn', aide:"Votre session est en pause. Adressez-vous à l'accueil.", libre:false },
  verrouille:          { txt:'Poste verrouillé',       dot:'warn', aide:"Ce poste est verrouillé par l'administrateur.", libre:false },
  desactive:           { txt:'Poste désactivé',        dot:'warn', aide:'Poste désactivé, contactez le gérant.', libre:false },
  hors_ligne:          { txt:'Connexion en cours…',    dot:'warn', aide:'', libre:false },
  serveur_injoignable: { txt:'Serveur injoignable',    dot:'',     aide:'Le serveur est injoignable. Nouvelle tentative automatique…', libre:false }
};

function afficherEtat() {
  const e = ETATS[etat] || ETATS.serveur_injoignable;
  $('etatTxt').textContent = e.txt;
  $('dot').className = 'dot ' + e.dot;
  if (!occupe) {
    bouton.disabled = !e.libre;
    bouton.textContent = e.libre ? 'ACTIVER' : 'INDISPONIBLE';
    if (msg.dataset.fixe !== '1') { msg.className = ''; msg.textContent = e.aide; }
  }
}

function message(texte, classe, fixe) {
  msg.className = classe || ''; msg.textContent = texte; msg.dataset.fixe = fixe ? '1' : '0';
}

bouton.addEventListener('click', () => {
  if (bouton.disabled || occupe) return;
  window.chrome.webview.postMessage('activer');
});

// ---------------- Discussion ----------------
const chat = $('chat'), liste = $('chatListe'), saisie = $('chatSaisie'), btnEnvoyer = $('chatEnvoyer');
let envoi = false;

function ouvrirChat(ouvert) {
  chat.classList.toggle('ouvert', ouvert);
  if (ouvert) { liste.scrollTop = liste.scrollHeight; setTimeout(() => saisie.focus(), 60); }
}
function ajouterMessage(m) {
  const v = $('chatVide'); if (v) v.remove();
  const d = document.createElement('div'); d.className = 'm ' + (m.moi ? 'moi' : 'eux');
  const b = document.createElement('div'); b.className = 'b'; b.textContent = m.texte || '';
  const h = document.createElement('div'); h.className = 'h'; h.textContent = (m.moi ? 'Moi' : 'Accueil') + ' · ' + (m.heure || '');
  d.append(b, h); liste.appendChild(d); liste.scrollTop = liste.scrollHeight;
}
function viderChat() {
  liste.innerHTML = '';
  const p = document.createElement('p'); p.className = 'vide'; p.id = 'chatVide'; p.textContent = 'Aucun message pour le moment.';
  liste.appendChild(p); ouvrirChat(false);
}
function envoyerChat() {
  const t = saisie.value.trim();
  if (!t || envoi) return;
  envoi = true; btnEnvoyer.disabled = true; $('chatErr').textContent = '';
  window.chrome.webview.postMessage('msg:' + t);
}
$('btnChat').addEventListener('click', () => ouvrirChat(!chat.classList.contains('ouvert')));
$('chatFermer').addEventListener('click', () => ouvrirChat(false));
btnEnvoyer.addEventListener('click', envoyerChat);
saisie.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); envoyerChat(); } });

// ---------------- Extinction ----------------
const arret = $('arret');
function majArret(r) {
  if (r > 0) {
    arret.classList.add('show');
    $('arretTxt').textContent = 'Extinction du poste dans';
    $('arretNum').textContent = r;
    $('arretAnnuler').style.display = '';
  } else if (r === 0) {
    arret.classList.add('show');
    $('arretTxt').textContent = 'Extinction en cours…';
    $('arretNum').textContent = '';
    $('arretAnnuler').style.display = 'none';
  } else {
    arret.classList.remove('show');
    message(r === -1 ? 'Extinction annulée.' : "L'extinction a échoué. Prévenez l'accueil.", r === -1 ? 'ok' : 'err', true);
    setTimeout(() => { msg.dataset.fixe = '0'; afficherEtat(); }, 6000);
  }
}
$('arretAnnuler').addEventListener('click', () => window.chrome.webview.postMessage('annuler_arret'));

// ---------------- Messages venant de l'agent ----------------
window.chrome.webview.addEventListener('message', ev => {
  const d = ev.data || {};
  if (d.type === 'infos') {
    $('poste').textContent = d.poste || '—';
    $('ip').textContent = d.ip ? ('IP : ' + d.ip) : 'IP : —';
    etat = d.etat || 'serveur_injoignable';
    const b = $('bandeau'); b.textContent = d.bandeau || ''; b.style.display = d.bandeau ? 'block' : 'none';
    afficherEtat();
  } else if (d.type === 'activation') {
    if (d.statut === 'encours') {
      occupe = true; bouton.disabled = true;
      bouton.innerHTML = '<span class="spin"></span>Activation…';
      message('', '', false);
    } else if (d.statut === 'ok') {
      bouton.textContent = 'ACTIVÉ';
      message(d.message || 'Poste activé. Bonne navigation !', 'ok', true);
    } else {
      occupe = false;
      message(d.message || 'Activation impossible.', 'err', true);
      afficherEtat();
      setTimeout(() => { msg.dataset.fixe = '0'; afficherEtat(); }, 6000);
    }
  } else if (d.type === 'message') {
    ajouterMessage(d);
    if (!d.moi) ouvrirChat(true);        // message de l'accueil : on ouvre la discussion
  } else if (d.type === 'message_statut') {
    envoi = false; btnEnvoyer.disabled = false;
    if (d.ok) saisie.value = ''; else $('chatErr').textContent = d.erreur || "Message non envoyé.";
    saisie.focus();
  } else if (d.type === 'historique_vide') {
    viderChat();
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