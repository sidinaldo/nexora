import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { IntegracaoWebhook } from './webhook/webhook';
import { IntegracaoAnuncios } from './anuncios/anuncios';

export type AbaIntegracoes = 'webhook' | 'anuncios';

/** INTEGRAÇÕES — o que o Nexora conversa com o resto do mundo.
 *
 *  ===================== POR QUE ABA, E NÃO UMA TELA POR PARCEIRO (INT-4) =====================
 *  "Conversões de anúncio" e "webhook de saída" respondem à MESMA pergunta do cliente: o que este
 *  sistema fala com o que eu já uso. Uma tela por parceiro faria o menu crescer um item por
 *  integração — e o dono, que abre isto uma vez por trimestre, teria de aprender onde cada uma mora.
 *
 *  Mesmo movimento que a Captação fez com QR e formulário, pela mesma razão.
 *  ==========================================================================================
 *
 *  ===================== ESTE COMPONENTE FAZ CABEÇALHO E ABAS, E SÓ =====================
 *  Os dois painéis buscam os próprios dados e funcionam sozinhos — é assim que são testados, e é
 *  o que evita transformar o container num componente que sabe demais.
 *
 *  Não há resumo comum aqui, ao contrário da Captação: lá os dois números são comparáveis ("o
 *  panfleto trouxe mais que a landing page?"). Aqui, "entregas de webhook" e "conversões
 *  enviadas" não se comparam com nada — somá-los seria um número que não responde pergunta
 *  nenhuma.
 *  ====================================================================================== */
@Component({
  selector: 'app-integracoes',
  imports: [IntegracaoWebhook, IntegracaoAnuncios],
  templateUrl: './integracoes.html',
  styleUrl: './integracoes.css'
})
export class Integracoes implements OnInit {
  private rota = inject(ActivatedRoute);
  private router = inject(Router);

  /** O webhook abre por padrão: é a integração que já existia, e quem tem link salvo para
   *  `/integracoes` espera cair nela. */
  aba = signal<AbaIntegracoes>('webhook');

  ngOnInit() {
    const pedida = this.rota.snapshot.queryParamMap.get('aba');
    if (pedida === 'webhook' || pedida === 'anuncios') this.aba.set(pedida);
  }

  trocarAba(aba: AbaIntegracoes) {
    if (this.aba() === aba) return;
    this.aba.set(aba);

    // `replaceUrl`: trocar de aba não é navegação para o histórico. Sem isto, o botão "voltar" do
    // navegador percorreria as abas antes de sair da tela.
    this.router.navigate([], {
      relativeTo: this.rota,
      queryParams: { aba: aba === 'webhook' ? null : aba },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }
}
