import { OrigemLead } from './modelos';

/** O nome de cada origem na língua de quem lê — `meta_ads` é "Meta Ads", `manual` é "Cadastro
 *  manual".
 *
 *  ⚠️ UMA CÓPIA SÓ. Ele morava dentro do dashboard, privado, e a tela de importar precisava do
 *  mesmo mapa: copiar faria a origem nova aparecer com nome bonito num lugar e como `meta_ads` no
 *  outro — que foi exatamente o que aconteceu quando `meta_ads` entrou no servidor e o painel não
 *  soube. O `Record<OrigemLead, string>` é o que obriga: origem nova sem rótulo não compila. */
export const ROTULO_ORIGEM: Record<OrigemLead, string> = {
  instagram: 'Instagram',
  facebook: 'Facebook',
  whatsapp: 'WhatsApp',
  google: 'Google',
  site: 'Site',
  qrcode: 'QR Code',
  indicacao: 'Indicação',
  meta_ads: 'Meta Ads',
  manual: 'Cadastro manual',
  outro: 'Outro'
};
