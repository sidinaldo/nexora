/** Configuração de DESENVOLVIMENTO. Substitui o environment.ts no build de dev.
 *
 *  A porta tem que bater com o launchSettings.json da API: `dotnet run` sem argumentos usa
 *  o perfil "http" (http://localhost:5123). Rodando pelo perfil "https" (ou pela IDE), a
 *  porta é 7283 — troque aqui, e SÓ aqui.
 *
 *  HTTP em dev de propósito: o certificado autoassinado do HTTPS local complica o WebSocket
 *  do SignalR e o CORS com credenciais. A origem http://localhost:4200 já está liberada no
 *  CORS da API (seção Cors:Origens do appsettings). */
export const environment = {
  producao: false,

  apiBase: 'http://localhost:5123/api',
  hubBase: 'http://localhost:5123/hub'
};
