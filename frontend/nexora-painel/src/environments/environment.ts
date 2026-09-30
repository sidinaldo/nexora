/** Configuração de PRODUÇÃO (é o arquivo padrão). O build de desenvolvimento troca este
 *  arquivo pelo environment.development.ts — ver fileReplacements no angular.json.
 *
 *  A URL da API vive AQUI, nunca chumbada num serviço. No painel do Recupera ela está
 *  dentro do api-base.ts, apontando para uma porta que contradiz o comentário do próprio
 *  arquivo; a causa raiz é justamente não haver arquivo de environment.
 *
 *  ===================== POR QUE URL ABSOLUTA, E NÃO `/api` (INF-1) =====================
 *  Estas duas linhas eram caminhos RELATIVOS, escritas para painel e API atrás do mesmo
 *  domínio. O arranjo de produção é outro: o painel está no Cloudflare Pages e a API num
 *  VPS, em `appnexora.duckdns.org`.
 *
 *  Com o caminho relativo, o painel chamaria `seu-projeto.pages.dev/api/...` — que não
 *  existe. O sintoma é cruel de diagnosticar: a tela de login ABRE, e nenhuma requisição
 *  chega na API. Não há erro no servidor, porque nada chega até ele.
 *
 *  ⚠️ TROCAR ESTE DOMÍNIO EXIGE MEXER EM TRÊS LUGARES, não só aqui:
 *    1. estas duas linhas;
 *    2. `DOMINIO_API` no `.env.prod` do servidor (é o que o Caddy usa para pedir o
 *       certificado). Hoje o Tunnel cria o registro DNS sozinho ao salvar a rota;
 *    3. `PAINEL_URL` no `.env.prod`, se o domínio do PAINEL também mudar — ele alimenta
 *       `Cors:Origens` e `Email:BaseUrlPainel`.
 *
 *  Esquecer o item 3 barra o painel inteiro por CORS, e o navegador só diz "No
 *  'Access-Control-Allow-Origin' header is present" — que não aponta para cá.
 *
 *  A alternativa considerada e descartada foi servir o painel pelo MESMO Caddy da API
 *  (`handle /api/*` + `file_server`): dispensaria CORS e manteria o caminho relativo, ao
 *  custo de perder a CDN do Pages e de o build do painel entrar no deploy do VPS. Ver
 *  `docs/INF-1.md`, seção 1.
 *  ================================================================================== */
export const environment = {
  producao: true,

  // ⚠️ ISTO APONTAVA PARA `appnexora.duckdns.org` — o domínio do arranjo anterior, que morreu
  // junto com aquele VPS. O sintoma seria o mesmo que o `INF-1` já registrou uma vez: o painel
  // abre a tela de login, nenhuma requisição chega, e não há erro de servidor porque nada chega
  // até ele.
  //
  // Hoje a API vive atrás do Cloudflare Tunnel, e este endereço não passa por porta aberta
  // nenhuma: o `cloudflared` liga a VPS à borda de dentro para fora.
  apiBase: 'https://nexora-api.softioconsultoria.com.br/api',
  hubBase: 'https://nexora-api.softioconsultoria.com.br/hub'
};
