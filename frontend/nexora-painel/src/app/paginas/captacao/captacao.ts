import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { Canais } from '../canais/canais';
import { Formularios } from '../formularios/formularios';
import { CanaisServico } from '../../nucleo/servicos/canais.servico';
import { FormulariosServico } from '../../nucleo/servicos/formularios.servico';
import { ConversoesServico } from '../../nucleo/servicos/conversoes.servico';

export type AbaCaptacao = 'qr' | 'formularios';

/** CAPTAÇÃO — de onde os leads vêm.
 *
 *  ===================== POR QUE OS DOIS NUMA TELA SÓ =====================
 *  Formulário do site e QR/link respondem à MESMA pergunta do cliente, compartilham a mesma
 *  estatística e o mesmo modo de uso: criar, publicar em algum lugar, e depois olhar quantos
 *  leads vieram. Em telas separadas, comparar "o panfleto trouxe mais que a landing page?"
 *  exigia abrir duas telas e somar de cabeça.
 *
 *  ===================== O FORMULÁRIO VOLTOU, E O QR É O PADRÃO (INT-4) =====================
 *  O painel de formulários saiu da tela num bloco anterior, com uma razão boa: o cliente típico
 *  desta ferramenta não tem site nem alguém que cole HTML nele. Isso continua verdade — por isso
 *  a aba que abre é a do QR.
 *
 *  O que mudou é o que o formulário passou a valer. É ele que carrega o rastro do anúncio
 *  (`utm_*`, `fbclid`, os cookies do pixel) para dentro do CRM, e é o que permite dizer à Meta
 *  qual anúncio trouxe a venda. Sem tela, quem já tinha publicado um formulário não conseguia
 *  ver a chave, nem desligá-lo, nem pegar o código novo — a rota `/formularios` só redirecionava
 *  para cá, apesar de um comentário afirmar que ela continuava acessível.
 *  ==========================================================================================
 *
 *  ===================== O QUE ESTE COMPONENTE FAZ, E SÓ =====================
 *  Cabeçalho, resumo e abas. As duas listas continuam sendo os componentes que já existiam —
 *  eles perderam o cabeçalho de página e viraram PAINÉIS. Copiar o conteúdo deles para cá teria
 *  criado uma terceira cópia de regras que já estavam prontas e testadas.
 *  ======================================================================== */
@Component({
  selector: 'app-captacao',
  imports: [Canais, Formularios, RouterLink],
  templateUrl: './captacao.html',
  styleUrl: './captacao.css'
})
export class Captacao implements OnInit {
  private canaisServico = inject(CanaisServico);
  private formulariosServico = inject(FormulariosServico);
  private conversoesServico = inject(ConversoesServico);
  private rota = inject(ActivatedRoute);
  private router = inject(Router);

  aba = signal<AbaCaptacao>('qr');

  // ---- o resumo
  leadsCanais = signal(0);
  canaisAtivos = signal(0);
  totalCanais = signal(0);

  leadsFormularios = signal(0);
  formulariosAtivos = signal(0);
  totalFormularios = signal(0);

  carregandoResumo = signal(true);

  /** ===================== O AVISO DE ANÚNCIO SE PERDENDO (INT-4) =====================
   *  Quantos leads dos últimos 30 dias chegaram com identificador de clique enquanto ninguém
   *  conectou a Meta.
   *
   *  ⚠️ AQUI, e não só no passo de "Primeiros passos": aquele painel some depois que o dono o
   *  fecha, e quem já é cliente há meses nunca mais o vê. Esta é a tela onde ele pensa em "de onde
   *  vêm meus leads" — é o momento em que a frase vale.
   *  ================================================================================= */
  leadsComAnuncioPerdidos = signal(0);

  total = computed(() => this.leadsCanais() + this.leadsFormularios());

  /** A fatia de cada caminho no total. Zero leads = zero, e não NaN — a tela nasce vazia. */
  fatiaCanais = computed(() =>
    this.total() === 0 ? 0 : Math.round((this.leadsCanais() / this.total()) * 100));
  fatiaFormularios = computed(() => this.total() === 0 ? 0 : 100 - this.fatiaCanais());

  ngOnInit() {
    // Aba pela URL: `/captacao?aba=formularios`. É o que faz o link antigo de `/formularios`
    // chegar na aba certa em vez de na primeira, e o que permite mandar "abre em Captação, aba
    // Formulário" por mensagem.
    const pedida = this.rota.snapshot.queryParamMap.get('aba');
    if (pedida === 'qr' || pedida === 'formularios') this.aba.set(pedida);

    this.carregarResumo();
  }

  trocarAba(aba: AbaCaptacao) {
    if (this.aba() === aba) return;
    this.aba.set(aba);

    // `replaceUrl`: trocar de aba não é navegação para o histórico. Sem isto, o botão "voltar"
    // do navegador percorreria as abas antes de sair da tela.
    this.router.navigate([], {
      relativeTo: this.rota,
      queryParams: { aba: aba === 'qr' ? null : aba },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  /** Os dois totais, numa leitura só.
   *
   *  ===================== POR QUE O RESUMO BUSCA OS DOIS =====================
   *  A aba aberta já busca a própria lista. O resumo busca as DUAS porque ele existe justamente
   *  para comparar — mostrar só o caminho visível seria a mesma tela de antes, com um número a
   *  mais. São dois GET de configuração, com no máximo algumas dezenas de linhas cada.
   *
   *  `catchError` por ramo: lista que falha vira número zerado em vez de tela presa em
   *  "Carregando…", e o painel abaixo mostra o próprio erro. Um erro num dos dois não apaga o
   *  outro.
   *  ========================================================================= */
  carregarResumo() {
    this.carregandoResumo.set(true);

    forkJoin({
      canais: this.canaisServico.listar().pipe(
        catchError(() => of({ itens: [], conexoes: [], podeCriar: false, leadsAtribuidos: 0 }))),
      formularios: this.formulariosServico.listar().pipe(catchError(() => of([]))),
      // O resumo é de CONFIGURAÇÃO e o vendedor não chega nesta tela; mesmo assim o `catchError`
      // fica, porque um 403 não pode apagar os dois números que a tela existe para mostrar.
      conversoes: this.conversoesServico.resumo().pipe(
        catchError(() => of({ enviando: true, leadsComAnuncio30Dias: 0 })))
    }).subscribe(r => {
      this.totalCanais.set(r.canais.itens.length);
      this.canaisAtivos.set(r.canais.itens.filter(c => c.ativo).length);
      this.leadsCanais.set(r.canais.leadsAtribuidos);

      this.totalFormularios.set(r.formularios.length);
      this.formulariosAtivos.set(r.formularios.filter(f => f.ativo).length);
      this.leadsFormularios.set(r.formularios.reduce((s, f) => s + f.leadsRecebidos, 0));

      // Só quando NÃO está enviando: dizer "você está perdendo 12 leads" para quem já conectou
      // seria mentira, e a próxima frase da tela perderia crédito junto.
      this.leadsComAnuncioPerdidos.set(
        r.conversoes.enviando ? 0 : r.conversoes.leadsComAnuncio30Dias);

      this.carregandoResumo.set(false);
    });
  }
}
