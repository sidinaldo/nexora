/** Configuração de PRODUÇÃO (é o arquivo padrão). O build de desenvolvimento troca este
 *  arquivo pelo environment.development.ts — ver fileReplacements no angular.json.
 *
 *  A URL da API vive AQUI, nunca chumbada num serviço. No painel do Recupera ela está
 *  dentro do api-base.ts, apontando para uma porta que contradiz o comentário do próprio
 *  arquivo; a causa raiz é justamente não haver arquivo de environment. */
export const environment = {
  producao: true,

  /** Em produção o painel e a API ficam atrás do mesmo domínio, então caminho relativo:
   *  evita CORS e evita ter que reconstruir o bundle para cada ambiente. */
  apiBase: '/api',
  hubBase: '/hub'
};
